using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using ReversedProxy.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ReversedProxy.Middlewares
{
    public class ReverseProxyMiddleware
    {
        private static readonly HttpClient _httpClient = new();
        private readonly RequestDelegate _nextMiddleware;
        private readonly SegmentsRedirectionTableOptions _segmentsRedirectionOptions;
        private readonly PathReplacementTableOptions _pathReplacementTableOptions;
        public ReverseProxyMiddleware(RequestDelegate nextMiddleware,
            IOptions<SegmentsRedirectionTableOptions> segmentsRedirectionOptions,
            IOptions<PathReplacementTableOptions> pathReplacementTableOptions
            )
        {
            _segmentsRedirectionOptions = segmentsRedirectionOptions?.Value ?? throw new ArgumentNullException(nameof(segmentsRedirectionOptions));
            _pathReplacementTableOptions = pathReplacementTableOptions?.Value ?? throw new ArgumentNullException(nameof(pathReplacementTableOptions));
            _nextMiddleware = nextMiddleware;
        }
        public async Task Invoke(HttpContext context)
        {
            var targetUri = BuildTargetUri(context.Request);

            if (targetUri is not null)
            {
                var targetRequestMessage = CreateTargetMessage(context, targetUri);
                using var responseMessage = await _httpClient.SendAsync(targetRequestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                var host = context.Request.Host.ToString();
                await ProcessResponseContent(responseMessage, context.Request.Host.ToString()).ConfigureAwait(false);
                context.Response.StatusCode = (int)responseMessage.StatusCode;
                CopyFromTargetResponseHeaders(context, responseMessage);
                await responseMessage.Content.CopyToAsync(context.Response.Body);

                return;
            }
            await _nextMiddleware(context);
        }
        private Uri BuildTargetUri(HttpRequest request)
        {
            if (!request.Path.HasValue) return null;

            string uri = null;

            foreach (var segment in _segmentsRedirectionOptions.SegmentsRedirectionTable)
            {
                if (request.Path.StartsWithSegments(segment.Key, out PathString remainingPath))
                {
                    uri = segment.Value + remainingPath;
                    break;
                }
            }

            if (string.IsNullOrEmpty(uri))
                return null;

            if (request.Query.Count > 0)
            {
                var parameters = request.Query.ToDictionary(x => x.Key, x => x.Value);
                return new Uri(QueryHelpers.AddQueryString(uri, parameters));
            }

            return new Uri(uri);
        }
        private static HttpRequestMessage CreateTargetMessage(HttpContext context, Uri targetUri)
        {
            var requestMessage = new HttpRequestMessage();
            CopyFromOriginalRequestHeaders(context, requestMessage);
            requestMessage.RequestUri = targetUri;
            requestMessage.Headers.Host = targetUri.Host;
            requestMessage.Method = GetMethod(context.Request.Method);
            return requestMessage;
        }
        private static void CopyFromOriginalRequestHeaders(HttpContext context, HttpRequestMessage requestMessage)
        {
            var requestMethod = context.Request.Method;

            if (!HttpMethods.IsGet(requestMethod) &&
              !HttpMethods.IsHead(requestMethod) &&
              !HttpMethods.IsDelete(requestMethod) &&
              !HttpMethods.IsTrace(requestMethod))
            {
                requestMessage.Content = new StreamContent(context.Request.Body);
            }

            foreach (var header in context.Request.Headers)
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        private static HttpMethod GetMethod(string method)
        {
            if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
            if (HttpMethods.IsGet(method)) return HttpMethod.Get;
            if (HttpMethods.IsHead(method)) return HttpMethod.Head;
            if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
            if (HttpMethods.IsPost(method)) return HttpMethod.Post;
            if (HttpMethods.IsPut(method)) return HttpMethod.Put;
            if (HttpMethods.IsTrace(method)) return HttpMethod.Trace;
            return new HttpMethod(method);
        }
        private async Task ProcessResponseContent(HttpResponseMessage responseMessage, string host)
        {
            if (!(IsContentOfType(responseMessage, "text/html") ||
                IsContentOfType(responseMessage, "application/javascript") ||
                IsContentOfType(responseMessage, "text/javascript") ||
                IsContentOfType(responseMessage, "text/xml") ||
                IsContentOfType(responseMessage, "application/json")))
            {
                return;
            }
            string stringContent = await GetStringContent(responseMessage).ConfigureAwait(false);
            stringContent = ReplaceUris(stringContent, host);
            responseMessage.Content = new StringContent(stringContent, Encoding.UTF8, responseMessage.Content.Headers.ContentType.MediaType);
        }
        private static bool IsContentOfType(HttpResponseMessage responseMessage, string type)
        {
            var result = false;
            if (responseMessage.Content?.Headers?.ContentType != null)
            {
                result = responseMessage.Content.Headers.ContentType.MediaType == type;
            }
            return result;
        }
        private static async Task<string> GetStringContent(HttpResponseMessage responseMessage)
        {
            var content = await responseMessage.Content.ReadAsByteArrayAsync();
            return Encoding.UTF8.GetString(content);
        }
        private string ReplaceUris(string stringContent, string host)
        {
            foreach (var path in _pathReplacementTableOptions.PathReplacementTable.OrderByDescending(x => x.Key))
            {
                if (path.Key.Contains("host"))
                {
                    stringContent = stringContent.Replace(path.Value, "https://" + path.Key.Replace("host", host));
                }
                else
                {
                    stringContent = stringContent.Replace(path.Value, path.Key);

                }
            }
            return stringContent;
        }
        private static void CopyFromTargetResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
        {
            foreach (var header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            context.Response.Headers.Remove("transfer-encoding");
        }
    }
}
