using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
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
                        if (additionalData.TryGetValue(Constants.AdditionalData.OrderReference, out string orderReference))
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
            string hmacKey = settings.HmacKey;

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

                        var hmacValidator = new Adyen.Util.HmacValidator();
                        var handler = new Adyen.Notification.NotificationHandler();
                        var notification = handler.HandleNotificationRequest(json);

                        bool? liveMode = notification.Live.TryParse<bool>();

                        // Check live mode from notification is opposite of test mode setting.
                        if (liveMode.HasValue && liveMode.Value != settings.TestMode)
                        {
                            foreach (var notificationRequestItemContainer in notification.NotificationItemContainers)
                            {
                                var notificationItem = notificationRequestItemContainer.NotificationItem;
                                if (hmacValidator.IsValidHmac(notificationItem, hmacKey))
                                {
                                    SendNotificationReceivedMessage();

                                    // Process the notification based on the eventCode
                                    string eventCode = notificationItem.EventCode;

                                    // If webhook notification has been configurated with Basic Auth (username and password), 
                                    // we need to verify this in header.
                                    var authHeader = request.Headers["Authorization"];
                                    if (authHeader != null)
                                    {
                                        var authHeaderVal = AuthenticationHeaderValue.Parse(authHeader);

                                        // https://docs.microsoft.com/en-us/aspnet/web-api/overview/security/basic-authentication

                                        // RFC 2617 sec 1.2, "scheme" name is case-insensitive
                                        if (authHeaderVal.Scheme.Equals("basic", StringComparison.OrdinalIgnoreCase) &&
                                            authHeaderVal.Parameter != null)
                                        {
                                            AuthenticateUser(authHeaderVal.Parameter, settings);
                                        }
                                    }

                                    adyenEvent = notificationItem;

                                    HttpContext.Current.Items["Vendr_AdyenEvent"] = adyenEvent;
                                }
                                else
                                {
                                    // Non valid NotificationRequest
                                    Vendr.Log.Warn<AdyenPaymentProviderBase<TSettings>>($"Failed verifying HMAC key for {notificationItem.PspReference}.");
                                }
                            }
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

        protected Adyen.Client GetClient(AdyenSettingsBase settings)
        {
            var config = new Adyen.Config
            {
                MerchantAccount = settings.MerchantAccount,
                XApiKey = settings.ApiKey,
                Environment = settings.TestMode
                    ? Adyen.Model.Enum.Environment.Test
                    : Adyen.Model.Enum.Environment.Live
            };

            // When using the overload method with config, it doesn't set environment, 
            // because it is possible to override, so we set the environment after to configurate the endpoints etc.
            // https://github.com/Adyen/adyen-dotnet-api-library/blob/master/Adyen/Client.cs

            var client = new Adyen.Client(config);
            client.SetEnvironment(config.Environment);

            return client;
        }

        protected string GetTransactionId(Adyen.Model.Modification.ModificationResult result)
        {
            return result.PspReference;
        }

        protected PaymentStatus GetPaymentStatus(Adyen.Model.Notification.NotificationRequestItem notification)
        {
            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodePending)
                return PaymentStatus.PendingExternalSystem;

            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCancellation)
                return PaymentStatus.Cancelled;

            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeCapture)
                return PaymentStatus.Captured;

            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeAuthorisation)
                return PaymentStatus.Authorized;

            return PaymentStatus.Initialized;
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

        protected void AuthenticateUser(string credentials, AdyenSettingsBase settings)
        {
            try
            {
                var encoding = Encoding.GetEncoding("ISO-8859-1");
                var bytes = Encoding.ASCII.GetBytes(Base64Decode(credentials));
                credentials = encoding.GetString(bytes);

                int separator = credentials.IndexOf(':');
                string username = credentials.Substring(0, separator);
                string password = credentials.Substring(separator + 1);

                bool validUser = username == settings.NotificationUsername && password == settings.NotificationPassword;
                if (!validUser)
                {
                    // Invalid username or password.
                    throw new AuthenticationException("Invalid username or password");
                }
            }
            catch (FormatException ex)
            {
                // Credentials were not formatted correctly.
                throw ex;
            }
        }

        private void SendNotificationReceivedMessage()
        {
            // Accept notifications: https://docs.adyen.com/development-resources/webhooks#accept-notifications
            HttpContext.Current.Response.Write("[accepted]");
        }
    }
}
