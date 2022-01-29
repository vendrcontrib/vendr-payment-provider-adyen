using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Vendr.Common.Logging;
using Vendr.Core.Api;
using Vendr.Core.Models;
using Vendr.Core.PaymentProviders;
using Vendr.Extensions;

namespace Vendr.Contrib.PaymentProviders.Adyen
{
    using Adyen = global::Adyen;

    public abstract class AdyenPaymentProviderBase<TSelf, TSettings> : PaymentProviderBase<TSettings>
        where TSelf : AdyenPaymentProviderBase<TSelf, TSettings>
        where TSettings : AdyenSettingsBase, new()
    {
        protected readonly ILogger<TSelf> _logger;

        public AdyenPaymentProviderBase(VendrContext vendr,
            ILogger<TSelf> logger)
            : base(vendr)
        {
            _logger = logger;
        }

        public override string GetCancelUrl(PaymentProviderContext<TSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.CancelUrl.MustNotBeNull("settings.CancelUrl");

            return ctx.Settings.CancelUrl;
        }
        public override string GetContinueUrl(PaymentProviderContext<TSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.ContinueUrl.MustNotBeNull("settings.ContinueUrl");

            return ctx.Settings.ContinueUrl;
        }

        public override string GetErrorUrl(PaymentProviderContext<TSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.ErrorUrl.MustNotBeNull("settings.ErrorUrl");

            return ctx.Settings.ErrorUrl;
        }

        public override async Task<OrderReference> GetOrderReferenceAsync(PaymentProviderContext<TSettings> ctx)
        {
            var adyenEvent = await GetWebhookAdyenEventAsync(ctx);
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
                        else
                        {
                            // Currently CANCELLATION notification doesn't include custom meta data in AdditionalData,
                            // so we use reference from notification data to lookup order.
                            if (!string.IsNullOrEmpty(adyenEvent.MerchantReference))
                            {
                                OrderReadOnly order = null;

                                var stores = Vendr.Services.StoreService.GetStores();

                                foreach (var store in stores)
                                {
                                    if (order != null)
                                        continue;

                                    // Try find order in store
                                    var foundOrder = Vendr.Services.OrderService.GetOrder(store.Id, adyenEvent.MerchantReference);
                                    if (foundOrder != null && foundOrder.TransactionInfo.PaymentStatus.HasValue)
                                    {
                                        var paymentStatus = foundOrder.TransactionInfo.PaymentStatus.Value;

                                        if (paymentStatus == PaymentStatus.Initialized ||
                                            paymentStatus == PaymentStatus.Authorized)
                                        {
                                            order = foundOrder;
                                        }
                                    }
                                }

                                if (order != null)
                                {
                                    return order.GenerateOrderReference();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Adyen - GetOrderReference");
                }
            }

            return await base.GetOrderReferenceAsync(ctx);
        }

        protected async Task<Adyen.Model.Notification.NotificationRequestItem> GetWebhookAdyenEventAsync(PaymentProviderContext<TSettings> ctx)
        {
            Adyen.Model.Notification.NotificationRequestItem adyenWebhookEvent = null;

            if (ctx.AdditionalData.ContainsKey("Vendr_AdyenWebhookEvent"))
            {
                adyenWebhookEvent = (Adyen.Model.Notification.NotificationRequestItem)ctx.AdditionalData["Vendr_AdyenWebhookEvent"];
            }
            else
            {
                adyenWebhookEvent = await ParseWebhookEventAsync(ctx.Request, ctx.Settings);

                ctx.AdditionalData.Add("Vendr_AdyenWebhookEvent", adyenWebhookEvent);
            }

            return adyenWebhookEvent;
        }

        private async Task<Adyen.Model.Notification.NotificationRequestItem> ParseWebhookEventAsync(HttpRequestMessage request, TSettings settings)
        {
            Adyen.Model.Notification.NotificationRequestItem adyenWebhookEvent = null;

            var headers = request.Content.Headers;

            using (var stream = await request.Content.ReadAsStreamAsync())
            {
                if (stream.CanSeek)
                    stream.Seek(0, SeekOrigin.Begin);

                using (var reader = new StreamReader(stream))
                {
                    var json = await reader.ReadToEndAsync();

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
                            if (hmacValidator.IsValidHmac(notificationItem, settings.HmacKey))
                            {
                                SendNotificationReceivedMessage();

                                // Process the notification based on the eventCode
                                string eventCode = notificationItem.EventCode;

                                // If webhook notification has been configurated with Basic Auth (username and password), 
                                // we need to verify this in header.
                                if (headers.TryGetValues("Authorization", out IEnumerable<string> headerValues))
                                {
                                    var authHeader = headerValues.FirstOrDefault();
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
                                }

                                adyenWebhookEvent = notificationItem;
                            }
                            else
                            {
                                // Non valid NotificationRequest
                                _logger.Warn($"Failed verifying HMAC key for {notificationItem.PspReference}.");
                            }
                        }
                    }
                }
            }

            return adyenWebhookEvent;
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

            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeRefund ||
                notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeRefundWithData)
                return PaymentStatus.Refunded;

            if (notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeAuthorisation ||
                notification.EventCode == Adyen.Model.Notification.NotificationRequestConst.EventCodeAuthorisationAdjustment)
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

        private HttpResponseMessage SendNotificationReceivedMessage()
        {
            // Accept notifications: https://docs.adyen.com/development-resources/webhooks#accept-notifications

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[accepted]") 
            };
        }
    }
}
