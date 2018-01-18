// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Docs.Build
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Hosting.Server.Features;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Features;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Docs.BuildCore;
    using Microsoft.Extensions.DependencyInjection;
    using Newtonsoft.Json.Linq;

    static class Hosting
    {
        public static IWebHost Create(
            Func<string, Task<(Stream stream, bool isDynamicRendering)>> readDocument,
            Func<string, Task<Stream>> readTemplate,
            int port = 0)
        {
            // TODO: Should have multiple site base path
            var siteBasePath = "docs";

            return WebHost
                .CreateDefaultBuilder()
                .UseUrls($"http://*:{port}")
                .ConfigureServices(services => services.AddRouting())
                .Configure(Configure)
                .Build();

            void Configure(IApplicationBuilder app)
            {
                app.UseRouter(BuildRoutes(app))
                   .Use(ServeDocuments)
                   .Use(ServeThemes);
            }

            IRouter BuildRoutes(IApplicationBuilder app)
            {
                return new RouteBuilder(app)
                   .MapGet("themes", GetAllThemes)
                   .MapGet("themes/{theme}/files/{*path}", GetThemeFile)
                   .MapGet("depots", GetAllDepotsBySite)
                   .MapGet("depots/{depot}/documents/{*path}", GetDocument)
                   .MapGet("depots/{depot}/potentialdocuments/{*path}", GetPotentialDocuments)
                   .MapGet("depots/{depot}/branches", GetBranches)
                   .MapGet("depots/{depot}/branches/{branch}", GetBranch)
                   .MapGet("monikers", GetMonikers)
                   .MapGet("familytrees/bymoniker/{moniker}", GetFamilyTreeByMoniker)
                   .MapGet("familytrees/byplatform/{platform}", GetFamiliesByPlatform)
                   .Build();
            }

            Task GetAllDepotsBySite(HttpContext http) => Write(http, new JObject
            {
                ["depots"] = new JArray(
                    new JObject
                    {
                        ["depot_name"] = $"MSDN.Docs",
                        ["site_base_path"] = siteBasePath.TrimEnd('/'),
                        ["priority"] = 0,
                        ["metadata"] = new JObject { ["theme"] = "Docs.Theme" },
                    })
            });

            Task GetAllThemes(HttpContext http) => Write(http, new JObject
            {
                ["themes"] = new JArray(
                    new JObject
                    {
                        ["site_name"] = "Docs",
                        ["theme_name"] = "Docs.Theme",
                        ["public_content_base_path_format"] = "/_themes/docs.theme/{branch_name}/{locale}",
                    })
            });

            async Task GetThemeFile(HttpContext http)
            {
                var path = Uri.UnescapeDataString(http.GetRouteValue("path").ToString());
                Debug.Assert(path.StartsWith("_themes/"));
                var templatePath = path.Substring("_themes/".Length).Replace('\\', '/');

                var stream = await readTemplate(templatePath);
                if (stream == null)
                {
                    await Write404(http);
                    return;
                }

                using (stream)
                {
                    await Write(http, new JObject
                    {
                        ["content_locale"] = "en-us",
                        ["content_uri"] = $"http://localhost:{port}/{path}"
                    });
                }
            }

            RequestDelegate ServeThemes(RequestDelegate next) => async http =>
            {
                if (http.Request.Path.StartsWithSegments("/_themes", out var remaining))
                {
                    using (var stream = await readTemplate(remaining.Value.Substring(1)))
                    {
                        if (stream != null)
                        {
                            http.Response.Headers["ETag"] = ETag();
                            stream.CopyTo(http.Response.Body);
                            return;
                        }
                    }
                }
                await next(http);
            };

            async Task GetDocument(HttpContext http)
            {
                var path = Uri.UnescapeDataString(http.GetRouteValue("path").ToString());
                var sitePath = Path.Combine(siteBasePath, path).Replace('\\', '/');
                var (stream, isDynamicRendering) = await readDocument(sitePath);

                if (stream == null)
                {
                    await Write404(http);
                    return;
                }

                using (stream)
                {
                    await Write(http, new JObject
                    {
                        ["asset_id"] = path,
                        ["locale"] = "en-us",
                        ["content_uri"] = $"http://localhost:{port}/{sitePath}",
                        ["metadata"] = new JObject {
                            ["is_dynamic_rendering"] = isDynamicRendering,
                            ["context"] = new JObject { }
                        },
                    });
                }
            }

            RequestDelegate ServeDocuments(RequestDelegate next) => async http =>
            {
                if (http.Request.Path.StartsWithSegments("/" + siteBasePath.TrimEnd('/'), out var _))
                {
                    var (stream, _) = await readDocument(http.Request.Path.Value.Substring(1));
                    if (stream != null)
                    {
                        using (stream)
                        {
                            http.Response.Headers["ETag"] = ETag();
                            stream.CopyTo(http.Response.Body);
                            return;
                        }
                    }
                }
                await next(http);
            };

            Task GetBranch(HttpContext http) => Write(http, new JObject
            {
                ["branches"] = new JArray(
                    new JObject
                    {
                        ["branch_name"] = "master",
                        ["theme_branch"] = "master",
                    })
            });

            Task GetBranches(HttpContext http) => Write(http, new JObject
            {
                ["branch_name"] = "master",
                ["theme_branch"] = "master",
            });

            Task GetPotentialDocuments(HttpContext http) => throw new NotImplementedException();

            Task GetMonikers(HttpContext http) => throw new NotImplementedException();
            Task GetFamilyTreeByMoniker(HttpContext http) => throw new NotImplementedException();
            Task GetFamiliesByPlatform(HttpContext http) => throw new NotImplementedException();
        }

        public static string GetAddress(this IFeatureCollection features)
            => features.Get<IServerAddressesFeature>().Addresses.First().Replace("[::]", "localhost");

        static string ETag()
            => $"\"{DateTime.UtcNow.ToString()}\"";

        static Task Write(HttpContext http, JObject json)
            => http.Response.WriteAsync(json.ToString());

        static Task Write404(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;

            // TODO: DHSClient requires an error contract or it'll throw NRE.
            return http.Response.WriteAsync("{}");
        }
    }
}
