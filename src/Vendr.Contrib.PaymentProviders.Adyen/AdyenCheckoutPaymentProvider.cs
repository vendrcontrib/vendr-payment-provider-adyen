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
        public override bool CanFetchPaymentStatus => false;

        // We'll finalize via webhook callback
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]{
            new TransactionMetaDataDefinition("adyenPaymentLinkId", "Adyen Payment Link ID"),
            new TransactionMetaDataDefinition("adyenPspReference", "Adyen PSP reference"),
            new TransactionMetaDataDefinition("adyenPaymentMethod", "Adyen Payment Method")
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

            var allowedPaymentMethods = settings.AllowedPaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToList();

            var blockedPaymentMethods = settings.BlockedPaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToList();

            var billingCountry = order.PaymentInfo.CountryId.HasValue
                    ? Vendr.Services.CountryService.GetCountry(order.PaymentInfo.CountryId.Value)
                    : null;

            var metadata = new Dictionary<string, string>
            {
                { "orderReference", order.GenerateOrderReference() },
                { "orderId", order.Id.ToString("D") },
                { "orderNumber", order.OrderNumber }
            };

            Adyen.Model.Checkout.PaymentLinkResource result = null;

            try
            {
                var client = GetClient(settings);

                // Create a payment request
                var amount = new Adyen.Model.Checkout.Amount(currencyCode, orderAmount);
                var paymentRequest = new Adyen.Model.Checkout.CreatePaymentLinkRequest
                    (
                        // Currently these are required in ctor
                        amount: amount,
                        merchantAccount: client.Config.MerchantAccount,
                        reference: order.OrderNumber
                    )
                {
                    ReturnUrl = continueUrl,
                    //MerchantOrderReference = order.GetOrderReference(),
                    ShopperEmail = order.CustomerInfo.Email,
                    ShopperReference = order.CustomerInfo.CustomerReference,
                    ShopperName = new Adyen.Model.Checkout.Name
                    (
                        firstName: order.CustomerInfo.FirstName,
                        lastName: order.CustomerInfo.LastName
                    ),
                    ShopperLocale = settings.Locale,
                    CountryCode = billingCountry?.Code,
                    Metadata = metadata
                };

                if (allowedPaymentMethods?.Count > 0)
                {
                    paymentRequest.AllowedPaymentMethods = allowedPaymentMethods;
                }

                if (blockedPaymentMethods?.Count > 0)
                {
                    paymentRequest.BlockedPaymentMethods = blockedPaymentMethods;
                }

                var checkout = new Adyen.Service.Checkout(client);

                result = checkout.PaymentLinks(paymentRequest);
            }
            catch (Adyen.HttpClient.HttpClientException ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, $"Request for payment failed::\n{ex.ResponseBody}\n");
                throw ex;
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, $"Request for payment failed::\n{ex.Message}\n");
                throw ex;
            }
            
            return new PaymentFormResult()
            {
                Form = new PaymentForm(result.Url, FormMethod.Get),
                MetaData = new Dictionary<string, string>
                {
                    { "adyenPaymentLinkId", result.Id },
                    { "adyenPspReference", result.Reference }
                }
            };
        }

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, AdyenCheckoutSettings settings)
        {
            // Check notification webhooks: https://docs.adyen.com/online-payments/pay-by-link#how-it-works
            try
            {
                // Match "additionalData.paymentLinkId" with payment link
                // https://docs.adyen.com/online-payments/pay-by-link?tab=api__2

                var adyenEvent = GetWebhookAdyenEvent(request, settings);
                if (adyenEvent != null)
                {
                    var amount = adyenEvent.Amount.Value ?? 0;

                    // PspReference = Unique identifier for the payment
                    var pspReference = adyenEvent.PspReference;

                    var metaData = new Dictionary<string, string>
                    {
                        { "adyenPspReference", pspReference },
                        { "adyenPaymentMethod", adyenEvent.PaymentMethod }
                    };

                    if (adyenEvent.AdditionalData.TryGetValue(Constants.AdditionalData.PaymentLinkId, out string paymentLinkId))
                    {
                        metaData.Add("adyenPaymentLinkId", paymentLinkId);
                    }

                    if (adyenEvent.Success)
                    {
                        if (adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeAuthorisation ||
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodePending ||
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCapture ||
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeRefund ||
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCancellation ||
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCancelOrRefund)
                        {
                            return CallbackResult.Ok(new TransactionInfo
                            {
                                TransactionId = pspReference,
                                AmountAuthorized = AmountFromMinorUnits(amount),
                                PaymentStatus = GetPaymentStatus(adyenEvent)
                            },
                            metaData);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override ApiResult CancelPayment(OrderReadOnly order, AdyenCheckoutSettings settings)
        {
            // Cancel: https://docs.adyen.com/online-payments/cancel

            try
            {
                var client = GetClient(settings);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Cancel(new Adyen.Model.Modification.CancelRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    OriginalReference = order.TransactionInfo.TransactionId
                    //Reference = "" (optional)
                });

                if (result.Response == Adyen.Model.Enum.ResponseEnum.CancelReceived ||
                    result.Response == Adyen.Model.Enum.ResponseEnum.CancelOrRefundReceived)
                {
                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(result),
                            PaymentStatus = GetPaymentStatus(result)
                        }
                    };
                }
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

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(order.TransactionAmount.Value);

            try
            {
                var client = GetClient(settings);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Capture(new Adyen.Model.Modification.CaptureRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = order.TransactionInfo.TransactionId
                    //Reference = "" (optional)
                });

                if (result.Response == Adyen.Model.Enum.ResponseEnum.CaptureReceived)
                {
                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(result),
                            PaymentStatus = GetPaymentStatus(result)
                        }
                    };
                }
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

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(order.TransactionAmount.Value);

            try
            {
                var client = GetClient(settings);

                var modification = new Adyen.Service.Modification(client);
                var result = modification.Refund(new Adyen.Model.Modification.RefundRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = order.TransactionInfo.TransactionId
                    //Reference = "" (optional)
                });

                if (result.Response == Adyen.Model.Enum.ResponseEnum.CancelReceived ||
                    result.Response == Adyen.Model.Enum.ResponseEnum.CancelOrRefundReceived)
                {
                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(result),
                            PaymentStatus = GetPaymentStatus(result)
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - RefundPayment");
            }

            return ApiResult.Empty;
        }
    }
}
