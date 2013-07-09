using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Cors;
using Microsoft.Owin;

namespace Microsoft.AspNet.SignalR.Owin.Middleware
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    public class CorsMiddleware
    {
        private static readonly string CorsRequestContextKey = "Microsoft.Owin.CorsRequest";

        private readonly AppFunc _next;
        private readonly CorsPolicy _policy;

        public CorsMiddleware(AppFunc next, CorsPolicy policy)
        {
            _next = next;
            _policy = policy;
        }

        public async Task Invoke(IDictionary<string, object> env)
        {
            var context = new OwinContext(env);
            CorsRequestContext corsRequestContext = GetCorsRequestContext(context);

            if (corsRequestContext != null)
            {
                try
                {
                    if (corsRequestContext.IsPreflight)
                    {
                        await HandleCorsPreflightRequestAsync(context, corsRequestContext);
                    }
                    else
                    {
                        await HandleCorsRequestAsync(context, corsRequestContext);
                    }
                }
                catch
                {
                    throw;
                }
            }
            else
            {
                await _next(env);
            }
        }

        public virtual async Task HandleCorsRequestAsync(IOwinContext context, CorsRequestContext corsRequestContext)
        {
            if (context == null)
            {
                throw new ArgumentNullException("request");
            }
            if (corsRequestContext == null)
            {
                throw new ArgumentNullException("corsRequestContext");
            }

            CorsResult result;
            if (TryEvaluateCorsPolicy(corsRequestContext, out result))
            {
                WriteCorsHeaders(context, result);
            }

            await _next(context.Environment);
        }

        private bool TryEvaluateCorsPolicy(CorsRequestContext corsRequestContext, out CorsResult result)
        {
            var engine = new CorsEngine();
            result = engine.EvaluatePolicy(corsRequestContext, _policy);
            return result != null && result.IsValid;
        }

        public virtual Task HandleCorsPreflightRequestAsync(IOwinContext context, CorsRequestContext corsRequestContext)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            if (corsRequestContext == null)
            {
                throw new ArgumentNullException("corsRequestContext");
            }


            //try
            //{
            //    // Make sure Access-Control-Request-Method is valid.
            //    new HttpMethod(corsRequestContext.AccessControlRequestMethod);
            //}
            //catch (ArgumentException)
            //{
            //    return request.CreateErrorResponse(HttpStatusCode.BadRequest,
            //            SRResources.AccessControlRequestMethodCannotBeNullOrEmpty);
            //}
            //catch (FormatException)
            //{
            //    return request.CreateErrorResponse(HttpStatusCode.BadRequest,
            //        String.Format(CultureInfo.CurrentCulture,
            //            SRResources.InvalidAccessControlRequestMethod,
            //            corsRequestContext.AccessControlRequestMethod));
            //}

            CorsResult result;
            if (TryEvaluateCorsPolicy(corsRequestContext, out result))
            {
                context.Response.StatusCode = 200;
                WriteCorsHeaders(context, result);
            }
            else
            {
                context.Response.StatusCode = 400;
            }

            return TaskAsyncHelper.Empty;
        }

        private void WriteCorsHeaders(IOwinContext context, CorsResult result)
        {
            IDictionary<string, string> corsHeaders = result.ToResponseHeaders();
            if (corsHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in corsHeaders)
                {
                    context.Response.Headers.Set(header.Key, header.Value);
                }
            }
        }

        private static CorsRequestContext GetCorsRequestContext(IOwinContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            object corsRequestContext;
            if (!context.Environment.TryGetValue(CorsRequestContextKey, out corsRequestContext))
            {
                if (!context.Request.Headers.ContainsKey(CorsConstants.Origin))
                {
                    return null;
                }

                CorsRequestContext requestContext = new CorsRequestContext
                {
                    RequestUri = context.Request.Uri,
                    HttpMethod = context.Request.Method,
                    Host = context.Request.Host,
                    Origin = context.Request.Headers.Get(CorsConstants.Origin),
                    AccessControlRequestMethod = context.Request.Headers.Get(CorsConstants.AccessControlRequestMethod)
                };

                IList<string> accessControlRequestHeaders = context.Request.Headers.GetValues(CorsConstants.AccessControlRequestHeaders);

                if (accessControlRequestHeaders != null)
                {
                    foreach (string accessControlRequestHeader in accessControlRequestHeaders)
                    {
                        if (accessControlRequestHeader != null)
                        {
                            IEnumerable<string> headerValues = accessControlRequestHeader.Split(',').Select(x => x.Trim());
                            foreach (string header in headerValues)
                            {
                                requestContext.AccessControlRequestHeaders.Add(header);
                            }
                        }
                    }
                }

                context.Environment.Add(CorsRequestContextKey, requestContext);
                corsRequestContext = requestContext;
            }

            return (CorsRequestContext)corsRequestContext;
        }
    }
}
