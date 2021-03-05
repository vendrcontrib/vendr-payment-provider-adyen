using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    public class AdyenCheckoutSettings : AdyenSettingsBase
    {
        [PaymentProviderSetting(Name = "Allowed Payment Methods",
            Description = "A comma separated list of payment methods to be presented to the shopper.",
            SortOrder = 1000)]
        public string AllowedPaymentMethods { get; set; }

        [PaymentProviderSetting(Name = "Blocked Payment Methods",
            Description = "A comma separated list of payment methods to be hidden from the shopper.",
            SortOrder = 1100)]
        public string BlockedPaymentMethods { get; set; }

        [PaymentProviderSetting(Name = "Locale",
            Description = "The language to be used in the payment page, specified by a combination of a language and country code.",
            SortOrder = 1200)]
        public string Locale { get; set; }
    }
}
