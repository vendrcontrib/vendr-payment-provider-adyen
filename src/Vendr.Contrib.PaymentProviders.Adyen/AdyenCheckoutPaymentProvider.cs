using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using Vendr.Core;
using Vendr.Core.Models;
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

        public override bool FinalizeAtContinueUrl => true;

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

            var metadata = new Dictionary<string, string>
            {
                //{ "orderReference", "" }
            };

            // Create a payment request
            var amount = new Adyen.Model.Checkout.Amount(currencyCode, orderAmount);
            var paymentRequest = new Adyen.Model.Checkout.CreatePaymentLinkRequest
            {
                Reference = order.OrderNumber,
                Amount = amount,
                ReturnUrl = settings.ContinueUrl,
                MerchantAccount = settings.MerchantAccount,
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

            if (paymentMethods.Count > 0)
            {
                paymentRequest.AllowedPaymentMethods = paymentMethods;
            }

            var environment = settings.TestMode ? Adyen.Model.Enum.Environment.Test : Adyen.Model.Enum.Environment.Live;

            // Create the http client
            var client = new Adyen.Client(settings.ApiKey, environment);
            var checkout = new Adyen.Service.Checkout(client);

            var paymentResponse = checkout.PaymentLinks(paymentRequest);

            return new PaymentFormResult()
            {
                Form = new PaymentForm(paymentResponse.Url, FormMethod.Post)
            };
        }

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, AdyenCheckoutSettings settings)
        {
            return new CallbackResult
            {
                TransactionInfo = new TransactionInfo
                {
                    AmountAuthorized = order.TransactionAmount.Value,
                    TransactionFee = 0m,
                    TransactionId = Guid.NewGuid().ToString("N"),
                    PaymentStatus = PaymentStatus.Authorized
                }
            };
        }
    }
}
