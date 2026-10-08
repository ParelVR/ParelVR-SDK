using System;

namespace ParelVR.SDK.Core.Http
{
    /// <summary>Thrown by ParelApiClient whenever the backend responds with success:false or a
    /// non-2xx status -- carries the server's own message field so calling UI can show it
    /// directly instead of a generic "request failed."</summary>
    public sealed class ParelApiException : Exception
    {
        public long StatusCode { get; }

        public ParelApiException(string message, long statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
