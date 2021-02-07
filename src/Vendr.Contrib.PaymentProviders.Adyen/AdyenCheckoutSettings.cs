using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    public class AdyenCheckoutSettings : AdyenSettingsBase
    {
        [PaymentProviderSetting(Name = "Accepted Payment Methods",
            Description = "A comma separated list of Payment Methods to accept.",
            SortOrder = 1000)]
        public string PaymentMethods { get; set; }
    }
}
