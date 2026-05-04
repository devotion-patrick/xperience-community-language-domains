using System;
using System.Threading.Tasks;

using CMS.Websites;
using CMS.Websites.Routing;

using Kentico.Content.Web.Mvc;
using Kentico.Content.Web.Mvc.Routing;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.Routing;

using XperienceCommunity.LanguageDomains.Internal;

namespace DancingGoat.Helpers
{
    public class LanguageLinkTagHelper : TagHelper
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IWebPageDataContextRetriever pageDataContextRetriever;
        private readonly IHtmlGenerator htmlGenerator;
        private readonly IWebPageUrlRetriever webPageUrlRetriever;
        private readonly IPreferredLanguageRetriever currentLanguageRetriever;
        private readonly CurrentWebsiteChannelPrimaryLanguageRetriever websiteChannelPrimaryLanguageRetriever;
        private readonly IWebsiteChannelContext channelContext;
        private readonly HostnameLookupIndex hostnameLookup;


        public string LinkText { get; set; }


        public string LanguageName { get; set; }


        [ViewContext]
        [HtmlAttributeNotBound]
        public ViewContext ViewContext { get; set; }


        public LanguageLinkTagHelper(
            IHttpContextAccessor httpContextAccessor,
            IWebPageDataContextRetriever pageDataContextRetriever,
            IHtmlGenerator htmlGenerator,
            IWebPageUrlRetriever webPageUrlRetriever,
            IPreferredLanguageRetriever currentLanguageRetriever,
            CurrentWebsiteChannelPrimaryLanguageRetriever websiteChannelPrimaryLanguageRetriever,
            IWebsiteChannelContext channelContext,
            HostnameLookupIndex hostnameLookup)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.pageDataContextRetriever = pageDataContextRetriever;
            this.htmlGenerator = htmlGenerator;
            this.webPageUrlRetriever = webPageUrlRetriever;
            this.currentLanguageRetriever = currentLanguageRetriever;
            this.websiteChannelPrimaryLanguageRetriever = websiteChannelPrimaryLanguageRetriever;
            this.channelContext = channelContext;
            this.hostnameLookup = hostnameLookup;
        }


        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            var httpContext = httpContextAccessor.HttpContext;

            // Page-context branch: ask Kentico for the page in the target
            // language. XperienceCommunity.LanguageDomains' URL retriever
            // decorator rewrites AbsoluteUrl onto the target language's
            // configured hostname, so we use it directly instead of
            // RelativePath (which would resolve against the *current* host).
            if (pageDataContextRetriever.TryRetrieve(out var webPageContext))
            {
                var url = await webPageUrlRetriever.Retrieve(webPageContext.WebPage.WebPageItemID, LanguageName);
                CreateActionLinkWithHref(output, url.AbsoluteUrl);
                return;
            }

            // Same-language link: keep the current URL exactly.
            if (currentLanguageRetriever.Get() == LanguageName)
            {
                var encoded = UriHelper.GetEncodedUrl(httpContext.Request);
                CreateActionLinkWithHref(output, encoded);
                return;
            }

            // Non-page routes (controller actions, search, etc.). Under the
            // hostname-based model, language is implicit in the host - so we
            // build an absolute URL on the target language's configured
            // hostname keeping the same path and query string. No language
            // prefix manipulation needed because each language is reachable
            // at the same controller route on its own host.
            var channelName = channelContext.WebsiteChannelName;
            if (!string.IsNullOrEmpty(channelName))
            {
                var lookup = hostnameLookup.FindForLanguage(channelName, LanguageName);
                if (lookup != null)
                {
                    var scheme = httpContext.Request.Scheme;
                    var path = httpContext.Request.Path;
                    var query = httpContext.Request.QueryString;
                    var absoluteUrl = $"{scheme}://{lookup.Hostname.Hostname}{path}{query}";
                    CreateActionLinkWithHref(output, absoluteUrl);
                    return;
                }
            }

            // Fall back to stock route-based switching when the package has
            // no configuration for this channel/language pair (mirrors the
            // out-of-box DancingGoat behavior).
            var originalRouteValues = httpContext.Request.RouteValues;
            var newRouteValues = new RouteValueDictionary(originalRouteValues);

            var queryParams = httpContext.Request.Query;
            foreach (var queryParam in queryParams)
            {
                var key = queryParam.Key;
                if (!string.IsNullOrEmpty(key))
                {
                    newRouteValues[key] = queryParams[key];
                }
            }

            output.TagName = null;
            var actionLink = await GenerateActionLink(newRouteValues);
            output.Content.SetHtmlContent(actionLink);
        }


        private void CreateActionLinkWithHref(TagHelperOutput output, string url)
        {
            output.TagName = "a";
            output.Attributes.Add("href", url);
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Content.SetContent(LinkText);
        }


        private async Task<TagBuilder> GenerateActionLink(RouteValueDictionary routeValues)
        {
            // Link for the primary language needs to be generated by route name in order to not put language prefix into query string
            var websiteChannelPrimaryLanguage = await websiteChannelPrimaryLanguageRetriever.Get();

            if (string.Equals(LanguageName, websiteChannelPrimaryLanguage, StringComparison.InvariantCultureIgnoreCase))
            {
                routeValues.Remove(WebPageRoutingOptions.LANGUAGE_ROUTE_VALUE_KEY);
                return htmlGenerator.GenerateRouteLink(ViewContext, LinkText, DancingGoatConstants.DEFAULT_ROUTE_WITHOUT_LANGUAGE_PREFIX_NAME, null, null, null, routeValues, null);
            }

            // Ensure correct language prefix
            routeValues[WebPageRoutingOptions.LANGUAGE_ROUTE_VALUE_KEY] = LanguageName;
            return htmlGenerator.GenerateActionLink(ViewContext, LinkText, null, null, null, null, null, routeValues, null);
        }
    }
}
