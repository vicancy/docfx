// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.BuildCore
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using Microsoft.DocAsCode.Dfm;
    using Microsoft.DocAsCode.MarkdownLite;
    using Newtonsoft.Json.Linq;
    static class MarkdownUtil
    {

        class MarkupInfo<T>
        {
            public JObject Metadata = new JObject();
            public HashSet<string> Dependencies = new HashSet<string>();
        }
        public static (string html, JObject metadata) Markup<T>(
            string content, ResolveContent<T> resolveContent, ResolveLink<T> resolveLink,
            T path, IReadOnlyDictionary<string, string> literals)
        {
            var info = new MarkupInfo<T>();
            var html = Markup(info, content, resolveContent, resolveLink, path, path, literals, false);
            return (html, info.Metadata);
        }

        static DfmEngineBuilder _dfm = GetDfmEngineBuilder();
        static DfmEngineBuilder GetDfmEngineBuilder()
        {
            var options = DocfxFlavoredMarked.CreateDefaultOptions();
            options.LegacyMode = true;
            options.XHtml = true;
            return new DfmEngineBuilder(options);
        }

        static string Markup<T>(
            MarkupInfo<T> info, string content, ResolveContent<T> resolveContent, ResolveLink<T> resolveLink,
            T path, T rootPath, IReadOnlyDictionary<string, string> replacementTokens, bool embed)
        {
            var renderer = new Renderer<T>(info, replacementTokens, path, rootPath, resolveContent, resolveLink);
            var engine = _dfm.CreateDfmEngine(renderer);
            var context = engine.Context.SetFilePathStack(ImmutableStack.Create(""));
            if (embed) context.SetIsInclude();
            return engine.Markup(content, context);
        }

        class Renderer<T> : DfmRenderer
        {
            readonly DfmCodeRenderer _codeRenderer = new DfmCodeRenderer();
            readonly MarkupInfo<T> _info;
            readonly ResolveContent<T> _resolveContent;
            readonly ResolveLink<T> _resolveLink;
            readonly T _rootPath;
            readonly T _path;

            public Renderer(
                MarkupInfo<T> info, IReadOnlyDictionary<string, string> tokens, T path, T rootPath,
                ResolveContent<T> resolve, ResolveLink<T> resolveLink)
            {
                _info = info;
                _resolveContent = resolve;
                _resolveLink = resolveLink;
                _path = path;
                _rootPath = rootPath;
                Tokens = tokens;
            }

            public override StringBuffer Render(IMarkdownRenderer renderer, DfmYamlHeaderBlockToken token, MarkdownBlockContext context)
            {
                _info.Metadata = (JObject)YamlUtil.Parse(token.Content);
                return StringBuffer.Empty;
            }

            public override StringBuffer Render(IMarkdownRenderer renderer, DfmIncludeBlockToken token, MarkdownBlockContext context)
                => RenderInclude(renderer, token.Src);
            public override StringBuffer Render(IMarkdownRenderer renderer, DfmIncludeInlineToken token, MarkdownInlineContext context)
                => RenderInclude(renderer, token.Src);

            public override StringBuffer Render(IMarkdownRenderer renderer, DfmFencesToken token, IMarkdownContext context)
            {
                if (_resolveContent == null) throw new InvalidOperationException("Don't know how to resolve include");

                var (content, path) = _resolveContent(_path, token.Path);
                var line = "";
                var lines = new List<string>();

                using (var sr = new StringReader(content))
                {
                    while ((line = sr.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }

#pragma warning disable CS0612 // Type or member is obsolete
                var code = _codeRenderer.ExtractCode(token, lines.ToArray());
#pragma warning restore CS0612
                return _codeRenderer.RenderFencesCode(token, renderer.Options, code.ErrorMessage, code.CodeLines);
            }

            public override StringBuffer Render(IMarkdownRenderer renderer, MarkdownImageInlineToken token, MarkdownInlineContext context)
                => Render(renderer, token, ResolveLink(token.Href), context);

            public override StringBuffer Render(IMarkdownRenderer renderer, MarkdownLinkInlineToken token, MarkdownInlineContext context)
                => Render(renderer, token, ResolveLink(token.Href), context);

            string ResolveLink(string href) => _resolveLink(_path, href, _rootPath);

            StringBuffer RenderInclude(IMarkdownRenderer renderer, string href)
            {
                if (_resolveContent == null) throw new InvalidOperationException("Don't know how to resolve include");
                if (!_info.Dependencies.Add(href)) throw new InvalidOperationException($"Circular include dependencies {string.Join(" --> ", _info.Dependencies)}");

                var (content, path) = _resolveContent(_path, href);

                return Markup(_info, content, _resolveContent, _resolveLink, path, _rootPath, Tokens, embed: true);
            }
        }
    }
}
