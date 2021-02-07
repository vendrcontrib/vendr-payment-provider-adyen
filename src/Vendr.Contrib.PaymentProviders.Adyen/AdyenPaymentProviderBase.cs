using System;
using System.IO;
using System.Linq;
using System.Web;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    using Adyen = global::Adyen;

    public abstract class AdyenPaymentProviderBase<TSettings> : PaymentProviderBase<TSettings>
        where TSettings : AdyenSettingsBase, new()
    {
        public AdyenPaymentProviderBase(VendrContext vendr)
            : base(vendr)
        { }

        public override string GetCancelUrl(OrderReadOnly order, TSettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.CancelUrl.MustNotBeNull("settings.CancelUrl");

            return settings.CancelUrl;
        }

        public override string GetContinueUrl(OrderReadOnly order, TSettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.ContinueUrl.MustNotBeNull("settings.ContinueUrl");

            return settings.ContinueUrl;
        }

        public override string GetErrorUrl(OrderReadOnly order, TSettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.ErrorUrl.MustNotBeNull("settings.ErrorUrl");

            return settings.ErrorUrl;
        }

        public override OrderReference GetOrderReference(HttpRequestBase request, TSettings settings)
        {
            var adyenEvent = GetWebhookAdyenEvent(request, settings);
            if (adyenEvent != null)
            {
                try
                {
                    var additionalData = adyenEvent.AdditionalData;
                    if (additionalData != null)
                    {
                        if (additionalData.TryGetValue("metadata.orderReference", out string orderReference))
                        {
                            return OrderReference.Parse(orderReference);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Vendr.Log.Error<AdyenCheckoutPaymentProvider>(ex, "Adyen - GetOrderReference");
                }
            }

            return base.GetOrderReference(request, settings);
        }

        protected Adyen.Model.Notification.NotificationRequestItem GetWebhookAdyenEvent(HttpRequestBase request, AdyenSettingsBase settings)
        {
            var environment = GetEnvironment(settings);

            Adyen.Model.Notification.NotificationRequestItem adyenEvent = null;

            if (HttpContext.Current.Items["Vendr_AdyenEvent"] != null)
            {
                adyenEvent = (Adyen.Model.Notification.NotificationRequestItem)HttpContext.Current.Items["Vendr_AdyenEvent"];
            }
            else
            {
                try
                {
                    if (request.InputStream.CanSeek)
                        request.InputStream.Seek(0, SeekOrigin.Begin);

                    using (var reader = new StreamReader(request.InputStream))
                    {
                        var json = reader.ReadToEnd();

                        var handler = new Adyen.Notification.NotificationHandler();
                        var notification = handler.HandleNotificationRequest(json);

                        // TODO: Check enviroment match notification.Live ("false" or "true")
                        // Verify "hmacSignature" in AdditionalData property
                        // Verify "orderReference" in "metadata.orderReference"

                        //Adyen.Model.Notification.NotificationRequestItem
                        //var hmacValidator = new Adyen.Util.HmacValidator();
                        //var encrypted = hmacValidator.CalculateHmac(data, key);

                        bool? liveMode = notification.Live.TryParse<bool>();

                        // Check live mode from notification is opposite of test mode setting.
                        if (liveMode.HasValue && liveMode.Value != settings.TestMode)
                        {
                            var item = notification.NotificationItemContainers[0].NotificationItem;

                            //var hmacValidator = new Adyen.Util.HmacValidator();
                            //var encrypted = hmacValidator.CalculateHmac(data, key);

                            adyenEvent = item;

                            HttpContext.Current.Items["Vendr_AdyenEvent"] = adyenEvent;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Vendr.Log.Error<AdyenPaymentProviderBase<TSettings>>(ex, "Adyen - GetWebhookAdyenEvent");
                }
            }

            return adyenEvent;
        }

        protected Adyen.Model.Enum.Environment GetEnvironment(AdyenSettingsBase settings)
        {
            return settings.TestMode 
                ? Adyen.Model.Enum.Environment.Test 
                : Adyen.Model.Enum.Environment.Live;
        }

        protected string GetTransactionId(Adyen.Model.Modification.ModificationResult result)
        {
            return result.PspReference;
        }

        protected PaymentStatus GetPaymentStatus(Adyen.Model.Modification.ModificationResult result)
        {
            if (result.Response == Adyen.Model.Enum.ResponseEnum.RefundReceived)
                return PaymentStatus.Refunded;

            if (result.Response == Adyen.Model.Enum.ResponseEnum.CaptureReceived)
                return PaymentStatus.Captured;

            if (result.Response == Adyen.Model.Enum.ResponseEnum.CancelReceived)
                return PaymentStatus.Cancelled;

            if (result.Response == Adyen.Model.Enum.ResponseEnum.AdjustAuthorisationReceived)
                return PaymentStatus.Authorized;

            return PaymentStatus.Initialized;
        }
    }
}
