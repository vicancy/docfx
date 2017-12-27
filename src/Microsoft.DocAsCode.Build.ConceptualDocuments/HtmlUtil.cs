// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.BuildCore
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Web;
    using HtmlAgilityPack;
    using Newtonsoft.Json.Linq;

    static class HtmlUtil
    {
        static readonly char[] s_delimChars = { ' ', '\t', '\n' };

        public static string ToMetaHtml(JObject metadata)
        {
            var sb = new StringBuilder();
            foreach (var meta in metadata)
            {
                sb.Append("<meta name=\"");
                sb.Append(HttpUtility.HtmlEncode(meta.Key));
                sb.Append("\" content=\"");
                sb.Append(HttpUtility.HtmlEncode(meta.Value));
                sb.Append("\" />");
            }
            return sb.ToString();
        }

        public static string PatchMetaHtml(string html, string key, string value)
        {
            var token = "<meta name=\"" + HttpUtility.HtmlEncode(key) + "\" content=\"";
            var begin = html.IndexOf(token);
            if (begin < 0) return html;

            var end = html.IndexOf('"', begin + 1);
            if (end < 0) return html;

            return html.Substring(0, begin + token.Length) + HttpUtility.HtmlEncode(value) + html.Substring(end);
        }

        public static (string title, string titleHtml, string contentHtml, int wordCount) ProcessHtml(string html, string locale)
        {
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(html);

            var (title, rawTitle, contentHtml) = SeparateHtml(doc.DocumentNode);
            var wordCount = WordCount(doc.DocumentNode);

            NormalizeHtmlLinks(contentHtml, locale);

            return (title, rawTitle, contentHtml.OuterHtml, wordCount);
        }

        public static int WordCount(HtmlNode html)
        {
            // TODO: word count does not work for CJK locales...
            return html.InnerText.Split(s_delimChars, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        public static (string title, string rawTitle, HtmlNode html) SeparateHtml(HtmlNode html)
        {
            // TODO: how to get TITLE
            // InnerText in HtmlAgilityPack is not decoded, should be a bug
            var headerNode = html.SelectSingleNode("//h1|//h2|//h3");
            var title = WebUtility.HtmlDecode(headerNode?.InnerText);
            var rawTitle = "";

            if (headerNode != null && GetFirstNoneCommentChild(html) == headerNode)
            {
                rawTitle = headerNode.OuterHtml;
                headerNode.Remove();
            }

            return (title, rawTitle, html);

            HtmlNode GetFirstNoneCommentChild(HtmlNode node)
            {
                var result = node.FirstChild;
                while (result != null && (result.NodeType == HtmlNodeType.Comment || string.IsNullOrWhiteSpace(result.OuterHtml)))
                {
                    result = result.NextSibling;
                }
                return result;
            }
        }

        public static void NormalizeHtmlLinks(HtmlNode html, string locale)
        {
            Process("a", "href", ToLower);

            // TODO: legacy behavior, img src not lowercased.
            Process("img", "src", _ => _);

            void Process(string name, string attr, Func<string, string> toLower)
            {
                foreach (var node in html.Descendants(name))
                {
                    var href = node.GetAttributeValue(attr, null);
                    if (string.IsNullOrEmpty(href)) continue;
                    if (href[0] == '#')
                    {
                        node.SetAttributeValue("data-linktype", "self-bookmark");
                        continue;
                    }
                    if (href.Contains(':'))
                    {
                        node.SetAttributeValue("data-linktype", "external");
                        continue;
                    }
                    if (href[0] == '/' || href[0] == '\\')
                    {
                        node.SetAttributeValue(attr, AddLocale(toLower(href)));
                        node.SetAttributeValue("data-linktype", "absolute-path");
                        continue;
                    }
                    node.SetAttributeValue(attr, ToLower(href));
                    node.SetAttributeValue("data-linktype", "relative-path");
                }
            }

            string ToLower(string href)
            {
                var i = href.IndexOfAny(new[] { '#', '?' });
                return i >= 0 ? href.Substring(0, i).ToLowerInvariant() + href.Substring(i) : href.ToLowerInvariant();
            }

            string AddLocale(string href)
            {
                try
                {
                    var pos = href.IndexOfAny(new[] { '/', '\\' }, 1);
                    for (var i = 0; i < pos; i++)
                    {
                        if (href[i] == '-')
                        {
                            CultureInfo.GetCultureInfo(href.Substring(1, pos - 1));
                            return href;
                        }
                    }
                    return '/' + locale + href;
                }
                catch (CultureNotFoundException)
                {
                    return '/' + locale + href;
                }
            }
        }
    }
}
