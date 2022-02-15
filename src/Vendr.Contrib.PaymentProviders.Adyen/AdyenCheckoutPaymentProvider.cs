using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vendr.Common.Logging;
using Vendr.Core.Api;
using Vendr.Core.Models;
using Vendr.Core.PaymentProviders;
using Vendr.Extensions;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    using Adyen = global::Adyen;

    [PaymentProvider("adyen-checkout", "Adyen Checkout", "Adyen payment provider for one time payments")]
    public class AdyenCheckoutPaymentProvider : AdyenPaymentProviderBase<AdyenCheckoutPaymentProvider, AdyenCheckoutSettings>
    {
        public AdyenCheckoutPaymentProvider(VendrContext vendr, ILogger<AdyenCheckoutPaymentProvider> logger)
            : base(vendr, logger)
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

        public override async Task<PaymentFormResult> GenerateFormAsync(PaymentProviderContext<AdyenCheckoutSettings> ctx)
        {
            var currency = Vendr.Services.CurrencyService.GetCurrency(ctx.Order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(ctx.Order.TransactionAmount.Value);

            var allowedPaymentMethods = ctx.Settings.AllowedPaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToList();

            var blockedPaymentMethods = ctx.Settings.BlockedPaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToList();

            var billingCountry = ctx.Order.PaymentInfo.CountryId.HasValue
                    ? Vendr.Services.CountryService.GetCountry(ctx.Order.PaymentInfo.CountryId.Value)
                    : null;

            var metadata = new Dictionary<string, string>
            {
                { "orderReference", ctx.Order.GenerateOrderReference() },
                { "orderId", ctx.Order.Id.ToString("D") },
                { "orderNumber", ctx.Order.OrderNumber }
            };

            Adyen.Model.Checkout.PaymentLinkResource result = null;

            try
            {
                var client = GetClient(ctx.Settings);

                // Create a payment request
                var amount = new Adyen.Model.Checkout.Amount(currencyCode, orderAmount);
                var paymentRequest = new Adyen.Model.Checkout.CreatePaymentLinkRequest
                    (
                        // Currently these are required in ctor
                        amount: amount,
                        merchantAccount: client.Config.MerchantAccount,
                        reference: ctx.Order.OrderNumber
                    )
                {
                    ReturnUrl = ctx.Urls.ContinueUrl,
                    ShopperEmail = ctx.Order.CustomerInfo.Email,
                    ShopperReference = ctx.Order.CustomerInfo.CustomerReference,
                    ShopperName = new Adyen.Model.Checkout.Name
                    (
                        firstName: ctx.Order.CustomerInfo.FirstName,
                        lastName: ctx.Order.CustomerInfo.LastName
                    ),
                    ShopperLocale = ctx.Settings.Locale,
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
                //result = await checkout.PaymentSessionAsync(paymentRequest);
            }
            catch (Adyen.HttpClient.HttpClientException ex)
            {
                _logger.Error(ex, $"Request for payment failed::\n{ex.ResponseBody}\n");
                throw ex;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Request for payment failed::\n{ex.Message}\n");
                throw ex;
            }
            
            return new PaymentFormResult()
            {
                Form = new PaymentForm(result.Url, PaymentFormMethod.Get),
                MetaData = new Dictionary<string, string>
                {
                    { "adyenPaymentLinkId", result.Id },
                    { "adyenPspReference", result.Reference }
                }
            };
        }

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<AdyenCheckoutSettings> ctx)
        {
            // Check notification webhooks: https://docs.adyen.com/online-payments/pay-by-link#how-it-works
            try
            {
                // Match "additionalData.paymentLinkId" with payment link
                // https://docs.adyen.com/online-payments/pay-by-link?tab=api__2

                var adyenEvent = await GetWebhookAdyenEventAsync(ctx);
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
                            adyenEvent.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCancellation)
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
                _logger.Error(ex, "Adyen - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override async Task<ApiResult> CancelPaymentAsync(PaymentProviderContext<AdyenCheckoutSettings> ctx)
        {
            // Cancel: https://docs.adyen.com/online-payments/cancel

            try
            {
                var client = GetClient(ctx.Settings);

                var modification = new Adyen.Service.Modification(client);
                var result = await modification.CancelAsync(new Adyen.Model.Modification.CancelRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    OriginalReference = ctx.Order.TransactionInfo.TransactionId,
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
                _logger.Error(ex, "Adyen - CancelPayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> CapturePaymentAsync(PaymentProviderContext<AdyenCheckoutSettings> ctx)
        {
            // Capture: https://docs.adyen.com/online-payments/capture#capture-a-payment

            var currency = Vendr.Services.CurrencyService.GetCurrency(ctx.Order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(ctx.Order.TransactionAmount.Value);

            try
            {
                var client = GetClient(ctx.Settings);

                var modification = new Adyen.Service.Modification(client);
                var result = await modification.CaptureAsync(new Adyen.Model.Modification.CaptureRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = ctx.Order.TransactionInfo.TransactionId
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
                _logger.Error(ex, "Adyen - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> RefundPaymentAsync(PaymentProviderContext<AdyenCheckoutSettings> ctx)
        {
            // Refund: https://docs.adyen.com/online-payments/refund

            var currency = Vendr.Services.CurrencyService.GetCurrency(ctx.Order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(ctx.Order.TransactionAmount.Value);

            try
            {
                var client = GetClient(ctx.Settings);

                var modification = new Adyen.Service.Modification(client);
                var result = await modification.RefundAsync(new Adyen.Model.Modification.RefundRequest
                {
                    MerchantAccount = client.Config.MerchantAccount,
                    ModificationAmount = new Adyen.Model.Amount(currencyCode, orderAmount),
                    OriginalReference = ctx.Order.TransactionInfo.TransactionId
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
                _logger.Error(ex, "Adyen - RefundPayment");
            }

            return ApiResult.Empty;
        }
    }
}
