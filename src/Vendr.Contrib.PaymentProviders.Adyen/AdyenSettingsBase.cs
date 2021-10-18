using Vendr.Core.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    public class AdyenSettingsBase
    {
        [PaymentProviderSetting(Name = "Continue URL",
            Description = "The URL to continue to after this provider has done processing. eg: /continue/",
            SortOrder = 100)]
        public string ContinueUrl { get; set; }

        [PaymentProviderSetting(Name = "Cancel URL",
            Description = "The URL to return to if the payment attempt is canceled. eg: /cancel/",
            SortOrder = 200)]
        public string CancelUrl { get; set; }

        [PaymentProviderSetting(Name = "Error URL",
            Description = "The URL to return to if the payment attempt errors. eg: /error/",
            SortOrder = 300)]
        public string ErrorUrl { get; set; }

        [PaymentProviderSetting(Name = "Merchant Account",
            Description = "Merchant Account used for payments.",
            SortOrder = 400)]
        public string MerchantAccount { get; set; }

        [PaymentProviderSetting(Name = "API Key",
            Description = "Acount specific API Key.",
            SortOrder = 500)]
        public string ApiKey { get; set; }

        [PaymentProviderSetting(Name = "HMAC Key",
            Description = "HMAC Key (HEX Encoded) for the notification.",
            SortOrder = 500)]
        public string HmacKey  { get; set; }

        [PaymentProviderSetting(Name = "Notification Username",
            Description = "User name for the notification.",
            SortOrder = 600)]
        public string NotificationUsername { get; set; }

        [PaymentProviderSetting(Name = "Notification Password",
            Description = "Password for the notification.",
            SortOrder = 700)]
        public string NotificationPassword { get; set; }

        [PaymentProviderSetting(Name = "Test Mode",
            Description = "Set whether to process payments in test mode.",
            SortOrder = 10000)]
        public bool TestMode { get; set; }
    }
}
