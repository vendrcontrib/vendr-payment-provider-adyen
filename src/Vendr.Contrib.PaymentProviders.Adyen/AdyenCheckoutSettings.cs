using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    public class AdyenCheckoutSettings : AdyenSettingsBase
    {
        [PaymentProviderSetting(Name = "Accepted Payment Methods",
            Description = "A comma separated list of Payment Methods to accept.",
            SortOrder = 1000)]
        public string PaymentMethods { get; set; }

        [PaymentProviderSetting(Name = "Test Mode",
            Description = "Set whether to process payments in test mode.",
            SortOrder = 10000)]
        public bool TestMode { get; set; }
    }
}
