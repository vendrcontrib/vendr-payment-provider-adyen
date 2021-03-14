namespace Vendr.Contrib.PaymentProviders.Adyen
{
    public class Constants
    {
        // Adyen constants in .NET library: https://github.com/Adyen/adyen-dotnet-api-library/blob/master/Adyen/Constants/AdditionalData.cs
        
        public class AdditionalData
        {
            // Additional constant for "paymentLinkId"
            public const string PaymentLinkId = "paymentLinkId";
            public const string MerchantOrderReference = "merchantOrderReference";

            // Custom constants for metadata
            public const string OrderReference = "metadata.orderReference";
            public const string OrderId = "metadata.orderId";
            public const string OrderNumber = "metadata.orderNumber";
        }
    }
}
