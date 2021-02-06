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

        protected Adyen.Model.Notification.NotificationRequestItem GetWebhookAdyenEvent(HttpRequestBase request, string webhookSigningSecret)
        {
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

                        //var handler = new Adyen.Notification.NotificationHandler();
                        //notification = handler.HandleNotificationRequest(json);

                        //Adyen.Model.Notification.NotificationRequestItem
                        //var hmacValidator = new Adyen.Util.HmacValidator();
                        //var ecnrypted = hmacValidator.CalculateHmac(data, key);


                        HttpContext.Current.Items["Vendr_AdyenEvent"] = adyenEvent;
                    }
                }
                catch (Exception ex)
                {
                    Vendr.Log.Error<AdyenPaymentProviderBase<TSettings>>(ex, "Adyen - GetWebhookAdyenEvent");
                }
            }

            return adyenEvent;
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
