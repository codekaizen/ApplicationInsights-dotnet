﻿namespace Microsoft.ApplicationInsights.Extensibility.Implementation.CorrelationLookup
{
    using System;
    using System.Collections.Concurrent;
    using System.Net;
    using Extensibility.Implementation.Tracing;

    internal class FailedRequestsManager
    {
        // Delay between trying to get Application Id once we get a failure while trying to get it. 
        // This is to throttle tries between failures to safeguard against performance hits. The impact would be that telemetry generated during this interval would not have x-component Correlation Id.
        private readonly TimeSpan retryWaitTimeSeconds;

        private ConcurrentDictionary<string, FailedResult> failingInstrumentationKeys = new ConcurrentDictionary<string, FailedResult>();

        internal FailedRequestsManager(int retryWaitTimeSeconds = 30)
        {
            this.retryWaitTimeSeconds = TimeSpan.FromSeconds(retryWaitTimeSeconds);
        }

        /// <summary>
        /// Registers failure for further action in future.
        /// </summary>
        /// <param name="instrumentationKey">Instrumentation key for which the failure occurred.</param>
        /// <param name="httpStatusCode">Response code from Application Id Endpoint.</param>
        public void RegisterFetchFailure(string instrumentationKey, HttpStatusCode httpStatusCode)
        {
            this.failingInstrumentationKeys.TryAdd(instrumentationKey, new FailedResult(this.retryWaitTimeSeconds, httpStatusCode));

            CoreEventSource.Log.CorrelationIdProviderFetchApplicationIdFailedWithResponseCode(httpStatusCode.ToString());
        }

        /// <summary>
        /// Registers failure for further action in future.
        /// </summary>
        /// <param name="instrumentationKey">Instrumentation Key for which the failure occurred.</param>
        /// <param name="ex">Exception indicating failure.</param>
        public void RegisterFetchFailure(string instrumentationKey, Exception ex)
        {
            if (ex is AggregateException ae)
            {
                var innerException = ae.Flatten().InnerException;
                if (innerException != null)
                {
                    this.RegisterFetchFailure(instrumentationKey, innerException);
                    return;
                }
            }
            else if (ex is WebException webException && webException.Response != null && webException.Response is HttpWebResponse httpWebResponse)
            {
                this.failingInstrumentationKeys.TryAdd(instrumentationKey, new FailedResult(this.retryWaitTimeSeconds, httpWebResponse.StatusCode));
            }
            else
            {
                this.failingInstrumentationKeys.TryAdd(instrumentationKey, new FailedResult(this.retryWaitTimeSeconds));
            }

            CoreEventSource.Log.CorrelationIdProviderFetchApplicationIdFailed(this.GetExceptionDetailString(ex));
        }

        public bool CanRetry(string instrumentationKey)
        {
            if (this.failingInstrumentationKeys.TryGetValue(instrumentationKey, out FailedResult lastFailedResult))
            {
                if (lastFailedResult.CanRetry())
                {
                    this.failingInstrumentationKeys.TryRemove(instrumentationKey, out FailedResult value);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private string GetExceptionDetailString(Exception ex)
        {
            if (ex is AggregateException ae)
            {
                return ae.Flatten().InnerException.ToInvariantString();
            }

            return ex.ToInvariantString();
        }

        /// <summary>
        /// Structure that represents a failed fetch Application Id call.
        /// </summary>
        private class FailedResult
        {
            private readonly DateTime retryAfterTime;
            private readonly bool shouldRetry;

            /// <summary>
            /// Initializes a new instance of the <see cref="FailedResult" /> class.
            /// </summary>
            /// <param name="retryAfter">Time to wait before a retry</param>
            /// <param name="httpStatusCode">Failure response code. Used to determine if we should retry requests.</param>
            public FailedResult(TimeSpan retryAfter, HttpStatusCode httpStatusCode = HttpStatusCode.OK)
            {
                this.retryAfterTime = DateTime.UtcNow + retryAfter;

                // Do not retry 4XX failures.
                var failureCode = (int)httpStatusCode;
                this.shouldRetry = !(failureCode >= 400 && failureCode < 500);
            }

            public bool CanRetry()
            {
                return this.shouldRetry && DateTime.UtcNow > this.retryAfterTime;
            }
        }
    }
}
