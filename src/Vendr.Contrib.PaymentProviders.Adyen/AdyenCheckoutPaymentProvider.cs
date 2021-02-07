using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    using Adyen = global::Adyen;

    [PaymentProvider("adyen-checkout", "Adyen Checkout", "Adyen payment provider for one time payments", Icon = "icon-invoice")]
    public class AdyenCheckoutPaymentProvider : AdyenPaymentProviderBase<AdyenCheckoutSettings>
    {
        public AdyenCheckoutPaymentProvider(VendrContext vendr)
            : base(vendr)
        { }

        public override bool CanCancelPayments => true;
        public override bool CanCapturePayments => true;
        public override bool CanRefundPayments => true;
        public override bool CanFetchPaymentStatus => true;

        // We'll finalize via webhook callback
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]{
            new TransactionMetaDataDefinition("adyenPspReference", "Adyen PSP reference")
        };

        public override PaymentFormResult GenerateForm(OrderReadOnly order, string continueUrl, string cancelUrl, string callbackUrl, AdyenCheckoutSettings settings)
        {
            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(order.TransactionAmount.Value);

            var paymentMethods = settings.PaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToList();

            var orderReference = order.GenerateOrderReference();

            var metadata = new Dictionary<string, string>
            {
                { "orderReference", orderReference }
            };

            // Create a payment request
            var amount = new Adyen.Model.Checkout.Amount(currencyCode, orderAmount);
            var paymentRequest = new Adyen.Model.Checkout.CreatePaymentLinkRequest
                (
                    // Currently these are required in ctor
                    amount: amount,
                    merchantAccount: settings.MerchantAccount,
                    reference: order.OrderNumber
                )
            {
                ReturnUrl = callbackUrl, //continueUrl,
                //MerchantOrderReference = order.GetOrderReference(),
                ShopperEmail = order.CustomerInfo.Email,
                ShopperReference = order.CustomerInfo.CustomerReference,
                ShopperName = new Adyen.Model.Checkout.Name
                (
                    firstName: order.CustomerInfo.FirstName,
                    lastName: order.CustomerInfo.LastName
                ),
                Metadata = metadata
            };

            if (paymentMethods?.Count > 0)
            {
                paymentRequest.AllowedPaymentMethods = paymentMethods;
            }

            var environment = GetEnvironment(settings);

            // Create the http client
            var client = new Adyen.Client(settings.ApiKey, environment);
            var checkout = new Adyen.Service.Checkout(client);

            var result = checkout.PaymentLinks(paymentRequest);

            return new PaymentFormResult()
            {
                Form = new PaymentForm(result.Url, FormMethod.Post),
                MetaData = new Dictionary<string, string>
                {
                    { "adyenPspReference", result.Reference }
                }
            };
        }

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, AdyenCheckoutSettings settings)
        {
            // Check notification webhooks: https://docs.adyen.com/online-payments/pay-by-link#how-it-works
            try
            {
                var adyenEvent = GetWebhookAdyenEvent(request, settings);
                if (adyenEvent != null && adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeAuthorisation)
                {
                    //var hmacValidator = new Adyen.Util.HmacValidator();
                    //var ecnrypted = hmacValidator.CalculateHmac(data, key);
                    var amount = adyenEvent.Amount.Value ?? 0;

                    var environment = GetEnvironment(settings);
                    var client = new Adyen.Client(settings.ApiKey, environment);

                    var payment = new Adyen.Service.Payment(client);
                    var result = payment.GetAuthenticationResult(new Adyen.Model.AuthenticationResultRequest
                    {
                        MerchantAccount = settings.MerchantAccount,
                        PspReference = adyenEvent.PspReference
                    });

                    return CallbackResult.Ok(new TransactionInfo
                    {
                        TransactionId = "",
                        AmountAuthorized = AmountFromMinorUnits(amount),
                        PaymentStatus = PaymentStatus.Authorized //GetPaymentStatus(result)
                    },
                    new Dictionary<string, string>
                    {
                        { "adyenPspReference", adyenEvent.PspReference }
                    });
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override ApiResult FetchPaymentStatus(OrderReadOnly order, AdyenCheckoutSettings settings)
        {
            try
            {
                var environment = GetEnvironment(settings);
                var client = new Adyen.Client(settings.ApiKey, environment);

                var payment = new Adyen.Service.Payment(client);
                var result = payment.GetAuthenticationResult(new Adyen.Model.AuthenticationResultRequest
                {
                    MerchantAccount = settings.MerchantAccount,
                    PspReference = order.Properties["adyenPspReference"]
                });

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = "", //GetTransactionId(result),
                        PaymentStatus = PaymentStatus.Authorized
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - FetchPaymentStatus");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CancelPayment(OrderReadOnly order, AdyenCheckoutSettings settings)
        {
            // Cancel: https://docs.adyen.com/online-payments/cancel

            try
            {
                var environment = GetEnvironment(settings);
                var client = new Adyen.Client(settings.ApiKey, environment);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Cancel(new Adyen.Model.Modification.CancelRequest
                {
                    MerchantAccount = settings.MerchantAccount,
                    OriginalReference = order.Properties["adyenPspReference"]
                    //Reference = "" (optional)
                });

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(result),
                        PaymentStatus = GetPaymentStatus(result)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - CancelPayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CapturePayment(OrderReadOnly order, AdyenCheckoutSettings settings)
        {
            // Capture: https://docs.adyen.com/online-payments/capture#capture-a-payment

            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            var orderAmount = AmountToMinorUnits(order.TransactionAmount.Value);

            try
            {
                var environment = GetEnvironment(settings);
                var client = new Adyen.Client(settings.ApiKey, environment);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Capture(new Adyen.Model.Modification.CaptureRequest
                {
                    MerchantAccount = settings.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = order.Properties["adyenPspReference"]
                    //Reference = "" (optional)
                });

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(result),
                        PaymentStatus = GetPaymentStatus(result)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult RefundPayment(OrderReadOnly order, AdyenCheckoutSettings settings)
        {
            // Refund: https://docs.adyen.com/online-payments/refund

            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            var orderAmount = AmountToMinorUnits(order.TransactionAmount.Value);

            try
            {
                var environment = GetEnvironment(settings);
                var client = new Adyen.Client(settings.ApiKey, environment);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Refund(new Adyen.Model.Modification.RefundRequest
                {
                    MerchantAccount = settings.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = order.Properties["adyenPspReference"]
                    //Reference = "" (optional)
                });

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(result),
                        PaymentStatus = GetPaymentStatus(result)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - RefundPayment");
            }

            return ApiResult.Empty;
        }
    }
}
