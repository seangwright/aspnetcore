// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Mvc.TagHelpers
{
    /// <summary>
    /// <see cref="TagHelper"/> implementation targeting &lt;cache&gt; elements.
    /// </summary>
    public class CacheTagHelper : CacheTagHelperBase
    {
        /// <summary>
        /// Prefix used by <see cref="CacheTagHelper"/> instances when creating entries in <see cref="MemoryCache"/>.
        /// </summary>
        public static readonly string CacheKeyPrefix = nameof(CacheTagHelper);
        private const string CachePriorityAttributeName = "priority";

        /// <summary>
        /// Creates a new <see cref="CacheTagHelper"/>.
        /// </summary>
        /// <param name="memoryCache">The <see cref="IMemoryCache"/>.</param>
        /// <param name="htmlEncoder">The <see cref="HtmlEncoder"/> to use.</param>
        public CacheTagHelper(IMemoryCache memoryCache, HtmlEncoder htmlEncoder) : base(htmlEncoder)
        {
            MemoryCache = memoryCache;
        }

        /// <summary>
        /// Gets the <see cref="IMemoryCache"/> instance used to cache entries.
        /// </summary>
        protected IMemoryCache MemoryCache { get; }

        /// <summary>
        /// Gets or sets the <see cref="CacheItemPriority"/> policy for the cache entry.
        /// </summary>
        [HtmlAttributeName(CachePriorityAttributeName)]
        public CacheItemPriority? Priority { get; set; }

        /// <inheritdoc />
        public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            
            IHtmlContent content = null;

            if (Enabled)
            {
                var key = GenerateKey(context);
                MemoryCacheEntryOptions options;
    
                while (content == null)
                {
                    Task<IHtmlContent> result = null;
                    
                    if (!MemoryCache.TryGetValue(key, out result))
                    {
                        var tokenSource = new CancellationTokenSource();

                        // Create an entry link scope and flow it so that any tokens related to the cache entries
                        // created within this scope get copied to this scope.

                        options = GetMemoryCacheEntryOptions();
                        options.AddExpirationToken(new CancellationChangeToken(tokenSource.Token));

                        var tcs = new TaskCompletionSource<IHtmlContent>();
                         
                        MemoryCache.Set(key, tcs.Task, options);
                        
                        try 
                        {
                            using (var link = MemoryCache.CreateLinkingScope())
                            {
                                result = ProcessContentAsync(output);
                                content = await result;
                                options.AddEntryLink(link);
                            }
                            
                            // The entry is set instead of assigning a value to the 
                            // task so that the expiration options are are not impacted 
                            // by the time it took to compute it.
                            
                            MemoryCache.Set(key, result, options);
                        }
                        catch
                        {
                            // Remove the worker task from the cache in case it can't complete.
                            tokenSource.Cancel();
                            throw;
                        }
                        finally
                        {
                            // If an exception occurs, ensure the other awaiters 
                            // render the output by themselves.
                            tcs.SetResult(null);
                        }
                    }
                    else
                    {
                        // There is either some value already cached (as a Task)
                        // or a worker processing the output. In the case of a worker,
                        // the result will be null, and the request will try to acquire
                        // the result from memory another time.

                        content = await result;
                    }
                }
            }
            else
            {
                content = await output.GetChildContentAsync();
            }

            // Clear the contents of the "cache" element since we don't want to render it.
            output.SuppressOutput();

            output.Content.SetContent(content);
        }

        // Internal for unit testing
        internal MemoryCacheEntryOptions GetMemoryCacheEntryOptions()
        {
            var options = new MemoryCacheEntryOptions();
            if (ExpiresOn != null)
            {
                options.SetAbsoluteExpiration(ExpiresOn.Value);
            }

            if (ExpiresAfter != null)
            {
                options.SetAbsoluteExpiration(ExpiresAfter.Value);
            }

            if (ExpiresSliding != null)
            {
                options.SetSlidingExpiration(ExpiresSliding.Value);
            }

            if (Priority != null)
            {
                options.SetPriority(Priority.Value);
            }

            return options;
        }

        protected override string GetUniqueId(TagHelperContext context)
        {
            return context.UniqueId;
        }

        protected override string GetKeyPrefix(TagHelperContext context)
        {
            return CacheKeyPrefix;
        }

        private async Task<IHtmlContent> ProcessContentAsync(TagHelperOutput output)
        {
            var content = await output.GetChildContentAsync();

            var stringBuilder = new StringBuilder();
            using (var writer = new StringWriter(stringBuilder))
            {
                content.WriteTo(writer, HtmlEncoder);
            }

            return new StringBuilderHtmlContent(stringBuilder);
        }

        private class StringBuilderHtmlContent : IHtmlContent
        {
            private readonly StringBuilder _builder;

            public StringBuilderHtmlContent(StringBuilder builder)
            {
                _builder = builder;
            }

            public void WriteTo(TextWriter writer, HtmlEncoder encoder)
            {
                for (var i = 0; i < _builder.Length; i++)
                {
                    writer.Write(_builder[i]);
                }
            }
        }
    }
}