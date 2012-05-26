using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using SquishIt.Framework.Resolvers;
using SquishIt.Framework.Renderers;
using SquishIt.Framework.Files;
using SquishIt.Framework.Utilities;
using System.IO;

namespace SquishIt.Framework.Base
{
    public abstract partial class BundleBase<T> where T : BundleBase<T>
    {
        protected IRenderer GetFileRenderer()
        {
            return debugStatusReader.IsDebuggingEnabled() ? new FileRenderer(fileWriterFactory) :
                releaseFileRenderer ??
                Configuration.Instance.DefaultReleaseRenderer() ??
                new FileRenderer(fileWriterFactory);
        }

        private List<string> GetFiles(List<Asset> assets)
        {
            var inputFiles = GetInputFiles(assets);
            var resolvedFilePaths = new List<string>();

            foreach(Input input in inputFiles)
            {
                resolvedFilePaths.AddRange(input.TryResolve(allowedExtensions, disallowedExtensions));
            }

            return resolvedFilePaths;
        }

        private Input GetInputFile(Asset asset)
        {
            if(!asset.IsEmbeddedResource)
            {
                if(debugStatusReader.IsDebuggingEnabled())
                {
                    return GetFileSystemPath(asset.LocalPath, asset.IsRecursive);
                }

                if(asset.IsRemoteDownload)
                {
                    return GetHttpPath(asset.RemotePath);
                }
                else
                {
                    return GetFileSystemPath(asset.LocalPath, asset.IsRecursive);
                }
            }
            else
            {
                return GetEmbeddedResourcePath(asset.RemotePath);
            }
        }

        private List<Input> GetInputFiles(List<Asset> assets)
        {
            var inputFiles = new List<Input>();
            foreach(var asset in assets)
            {
                inputFiles.Add(GetInputFile(asset));
            }
            return inputFiles;
        }

        private Input GetFileSystemPath(string localPath, bool isRecursive = true)
        {
            string mappedPath = FileSystem.ResolveAppRelativePathToFileSystem(localPath);
            return new Input(mappedPath, isRecursive, ResolverFactory.Get<FileSystemResolver>());
        }

        private Input GetHttpPath(string remotePath)
        {
            return new Input(remotePath, false, ResolverFactory.Get<HttpResolver>());
        }

        private Input GetEmbeddedResourcePath(string resourcePath)
        {
            return new Input(resourcePath, false, ResolverFactory.Get<EmbeddedResourceResolver>());
        }

        protected IEnumerable<IPreprocessor> FindPreprocessors(string file)
        {
            //using rails convention of applying preprocessing based on file extension components in reverse order
            return file.Split('.')
                .Skip(1)
                .Reverse()
                .Select(FindPreprocessor)
                .Where(p => p != null);
        }

        protected abstract string ProcessFile(string file, string outputFile);

        protected string PreprocessFile(string file, IEnumerable<IPreprocessor> preprocessors)
        {
            try
            {
                currentDirectoryWrapper.SetCurrentDirectory(Path.GetDirectoryName(file));
                var preprocessedContent = PreprocessContent(file, preprocessors, ReadFile(file));
                currentDirectoryWrapper.Revert();
                return preprocessedContent;
            }
            catch
            {
                currentDirectoryWrapper.Revert();
                throw;
            }
        }

        protected string PreprocessContent(string file, IEnumerable<IPreprocessor> preprocessors, string content)
        {
            if(preprocessors == null)
            {
                return content;
            }
            return preprocessors.Aggregate<IPreprocessor, string>(content, (cntnt, pp) => pp.Process(file, cntnt));
        }

        IPreprocessor FindPreprocessor(string extension)
        {
            var instanceTypes = instancePreprocessors.Select(ipp => ipp.GetType()).ToArray();

            return Bundle.Preprocessors.Where(pp => !instanceTypes.Contains(pp.GetType()))
                .Union(instancePreprocessors)
                .FirstOrDefault(p => p.ValidFor(extension));
        }

        private string ExpandAppRelativePath(string file)
        {
            if(file.StartsWith("~/"))
            {
                string appRelativePath = HttpRuntime.AppDomainAppVirtualPath;
                if(appRelativePath != null && !appRelativePath.EndsWith("/"))
                    appRelativePath += "/";
                return file.Replace("~/", appRelativePath);
            }
            return file;
        }

        protected string ReadFile(string file)
        {
            using(var sr = fileReaderFactory.GetFileReader(file))
            {
                return sr.ReadToEnd();
            }
        }

        protected bool FileExists(string file)
        {
            return fileReaderFactory.FileExists(file);
        }

        private string GetAdditionalAttributes(BundleState bundleState)
        {
            var result = new StringBuilder();
            foreach(var attribute in bundleState.Attributes)
            {
                result.Append(attribute.Key);
                result.Append("=\"");
                result.Append(attribute.Value);
                result.Append("\" ");
            }
            return result.ToString();
        }

        private string GetFilesForRemote(List<string> remoteAssetPaths, BundleState bundleState)
        {
            var sb = new StringBuilder();
            foreach(var uri in remoteAssetPaths)
            {
                sb.Append(FillTemplate(bundleState, uri));
            }

            return sb.ToString();
        }

        private void AddAsset(Asset asset)
        {
            bundleState.Assets.Add(asset);
        }

        public T WithoutTypeAttribute()
        {
            this.typeless = true;
            return (T)this;
        }

        string BuildAbsolutePath(string siteRelativePath)
        {
            if(HttpContext.Current == null)
                throw new InvalidOperationException("Absolute path can only be constructed in the presence of an HttpContext.");
            if(!siteRelativePath.StartsWith("/"))
                throw new InvalidOperationException("This helper method only works with site relative paths.");

            var url = HttpContext.Current.Request.Url;
            var port = url.Port != 80 ? (":" + url.Port) : String.Empty;
            return string.Format("{0}://{1}{2}{3}", url.Scheme, url.Host, port, VirtualPathUtility.ToAbsolute(siteRelativePath));
        }

        public string Render(string renderTo)
        {
            string key = renderTo;
            return Render(renderTo, key, GetFileRenderer());
        }

        private string Render(string renderTo, string key, IRenderer renderer)
        {
            var cacheUniquenessHash = key.Contains("#") ? hasher.GetHash(bundleState.Assets
                                               .Select(a => a.IsRemote ? a.RemotePath : a.LocalPath)
                                               .Union(bundleState.Arbitrary.Select(ac => ac.Content))
                                               .OrderBy(s => s)
                                               .Aggregate((acc, val) => acc + val)) : string.Empty;

            key = CachePrefix + key + cacheUniquenessHash;

            if(!String.IsNullOrEmpty(BaseOutputHref))
            {
                key = BaseOutputHref + key;
            }

            if(debugStatusReader.IsDebuggingEnabled())
            {
                var content = RenderDebug(renderTo, key, renderer);
                return content;
            }
            return RenderRelease(key, renderTo, renderer);
        }

        public string RenderNamed(string name)
        {
            bundleState = GetCachedBundleState(name);
            //TODO: this sucks
            // Revisit https://github.com/jetheredge/SquishIt/pull/155 and https://github.com/jetheredge/SquishIt/issues/183
            //hopefully we can find a better way to satisfy both of these requirements
            var fullName = (BaseOutputHref ?? "") + CachePrefix + name;
            var content = bundleCache.GetContent(fullName);
            if(content == null)
            {
                AsNamed(name, bundleState.Path);
                return bundleCache.GetContent(CachePrefix + name);
            }
            return content;
        }

        public string RenderCached(string name)
        {
            bundleState = GetCachedBundleState(name);
            var content = CacheRenderer.Get(CachePrefix, name);
            if(content == null)
            {
                AsCached(name, bundleState.Path);
                return CacheRenderer.Get(CachePrefix, name);
            }
            return content;
        }

        public string RenderCachedAssetTag(string name)
        {
            bundleState = GetCachedBundleState(name);
            return Render(null, name, new CacheRenderer(CachePrefix, name));
        }

        public void AsNamed(string name, string renderTo)
        {
            Render(renderTo, name, GetFileRenderer());
            bundleState.Path = renderTo;
            bundleStateCache[CachePrefix + name] = bundleState;
        }

        public string AsCached(string name, string filePath)
        {
            string result = Render(filePath, name, new CacheRenderer(CachePrefix, name));
            bundleState.Path = filePath;
            bundleStateCache[CachePrefix + name] = bundleState;
            return result;
        }

        string RenderDebug(string renderTo, string name, IRenderer renderer)
        {
            string content = null;

            DependentFiles.Clear();

            var renderedFiles = new HashSet<string>();

            BeforeRenderDebug();

            var sb = new StringBuilder();
            var attributes = GetAdditionalAttributes(bundleState);
            var assets = bundleState.Assets;

            DependentFiles.AddRange(GetFiles(assets));
            foreach(var asset in assets)
            {
                var inputFile = GetInputFile(asset);
                var files = inputFile.TryResolve(allowedExtensions, disallowedExtensions);

                if(asset.IsEmbeddedResource)
                {
                    var tsb = new StringBuilder();

                    foreach(var fn in files)
                    {
                        tsb.Append(ReadFile(fn) + "\n\n\n");
                    }

                    var processedFile = ExpandAppRelativePath(asset.LocalPath);
                    //embedded resources need to be rendered regardless to be usable
                    renderer.Render(tsb.ToString(), FileSystem.ResolveAppRelativePathToFileSystem(processedFile));
                    sb.AppendLine(FillTemplate(bundleState, processedFile));
                }
                else if(asset.RemotePath != null)
                {
                    sb.AppendLine(FillTemplate(bundleState, ExpandAppRelativePath(asset.LocalPath)));
                }
                else
                {
                    foreach(var file in files)
                    {
                        if(!renderedFiles.Contains(file))
                        {
                            var fileBase = FileSystem.ResolveAppRelativePathToFileSystem(asset.LocalPath);
                            var newPath = file.Replace(fileBase, "");
                            var path = ExpandAppRelativePath(asset.LocalPath + newPath.Replace("\\", "/"));
                            sb.AppendLine(FillTemplate(bundleState, path));
                            renderedFiles.Add(file);
                        }
                    }
                }
            }

            foreach(var cntnt in bundleState.Arbitrary)
            {
                var filename = "dummy" + cntnt.Extension;
                var preprocessors = FindPreprocessors(filename);
                var processedContent = PreprocessContent(filename, preprocessors, cntnt.Content);
                sb.AppendLine(string.Format(tagFormat, processedContent));
            }

            content = sb.ToString();

            if(bundleCache.ContainsKey(name))
            {
                bundleCache.Remove(name);
            }
            bundleCache.Add(name, content, DependentFiles);

            //need to render the bundle to caches, otherwise leave it
            if(renderer is CacheRenderer)
                renderer.Render(content, renderTo);

            return content;
        }

        private string RenderRelease(string key, string renderTo, IRenderer renderer)
        {
            string content;
            if(!bundleCache.TryGetValue(key, out content))
            {
                using(new CriticalRenderingSection(renderTo))
                {
                    if(!bundleCache.TryGetValue(key, out content))
                    {
                        var uniqueFiles = new List<string>();
                        string minifiedContent = null;
                        string hash = null;
                        bool hashInFileName = false;

                        DependentFiles.Clear();

                        if(renderTo == null)
                        {
                            renderTo = renderPathCache[CachePrefix + "." + key];
                        }
                        else
                        {
                            renderPathCache[CachePrefix + "." + key] = renderTo;
                        }

                        string outputFile = FileSystem.ResolveAppRelativePathToFileSystem(renderTo);
                        var renderToPath = ExpandAppRelativePath(renderTo);

                        if(!String.IsNullOrEmpty(BaseOutputHref))
                        {
                            renderToPath = String.Concat(BaseOutputHref.TrimEnd('/'), "/", renderToPath.TrimStart('/'));
                        }

                        var remoteAssetPaths = new List<string>();
                        foreach(var asset in bundleState.Assets)
                        {
                            if(asset.IsRemote)
                            {
                                remoteAssetPaths.Add(asset.RemotePath);
                            }
                        }

                        uniqueFiles.AddRange(GetFiles(bundleState.Assets.Where(asset =>
                            asset.IsEmbeddedResource ||
                            asset.IsLocal ||
                            asset.IsRemoteDownload).ToList()).Distinct());

                        string renderedTag = string.Empty;
                        if(uniqueFiles.Count > 0 || bundleState.Arbitrary.Count > 0)
                        {
                            DependentFiles.AddRange(uniqueFiles);

                            if(renderTo.Contains("#"))
                            {
                                hashInFileName = true;
                                minifiedContent = Minifier.Minify(BeforeMinify(outputFile, uniqueFiles, bundleState.Arbitrary));
                                hash = hasher.GetHash(minifiedContent);
                                renderToPath = renderToPath.Replace("#", hash);
                                outputFile = outputFile.Replace("#", hash);
                            }

                            if(ShouldRenderOnlyIfOutputFileIsMissing && FileExists(outputFile))
                            {
                                minifiedContent = ReadFile(outputFile);
                            }
                            else
                            {
                                minifiedContent = minifiedContent ?? Minifier.Minify(BeforeMinify(outputFile, uniqueFiles, bundleState.Arbitrary));
                                renderer.Render(minifiedContent, outputFile);
                            }

                            if(hash == null && !string.IsNullOrEmpty(HashKeyName))
                            {
                                hash = hasher.GetHash(minifiedContent);
                            }

                            if(hashInFileName)
                            {
                                renderedTag = FillTemplate(bundleState, renderToPath);
                            }
                            else
                            {
                                if(string.IsNullOrEmpty(HashKeyName))
                                {
                                    renderedTag = FillTemplate(bundleState, renderToPath);
                                }
                                else if(renderToPath.Contains("?"))
                                {
                                    renderedTag = FillTemplate(bundleState,
                                                               renderToPath + "&" + HashKeyName + "=" + hash);
                                }
                                else
                                {
                                    renderedTag = FillTemplate(bundleState,
                                                               renderToPath + "?" + HashKeyName + "=" + hash);
                                }
                            }
                        }

                        content += String.Concat(GetFilesForRemote(remoteAssetPaths, bundleState), renderedTag);
                    }
                }
                bundleCache.Add(key, content, DependentFiles);
            }

            return content;
        }

        public void ClearCache()
        {
            bundleCache.ClearTestingCache();
        }

        private void AddAttributes(Dictionary<string, string> attributes, bool merge = true)
        {
            if(merge)
            {
                foreach(var attribute in attributes)
                {
                    bundleState.Attributes[attribute.Key] = attribute.Value;
                }
            }
            else
            {
                bundleState.Attributes = attributes;
            }
        }

        protected string BeforeMinify(string outputFile, List<string> files, IEnumerable<ArbitraryContent> arbitraryContent)
        {
            var sb = new StringBuilder();

            files.Select(f => ProcessFile(f, outputFile))
                .Concat(arbitraryContent.Select(ac =>
                {
                    var filename = "dummy." + ac.Extension;
                    var preprocessors = FindPreprocessors(filename);
                    return PreprocessContent(filename, preprocessors, ac.Content);
                }))
                .Aggregate(sb, (builder, val) => builder.Append(val + "\n"));

            return sb.ToString();
        }

        void BeforeRenderDebug()
        {
            foreach(var asset in bundleState.Assets)
            {
                var localPath = asset.LocalPath;
                var preprocessors = FindPreprocessors(localPath);
                if(preprocessors != null && preprocessors.Count() > 0)
                {
                    var outputFile = FileSystem.ResolveAppRelativePathToFileSystem(localPath);
                    var appendExtension = ".debug" + defaultExtension.ToLowerInvariant();
                    string content;
                    lock(typeof(T))
                    {
                        content = PreprocessFile(outputFile, preprocessors);
                    }
                    outputFile += appendExtension;
                    using(var fileWriter = fileWriterFactory.GetFileWriter(outputFile))
                    {
                        fileWriter.Write(content);
                    }
                    asset.LocalPath = localPath + appendExtension;
                }
            }
        }

        private BundleState GetCachedBundleState(string name)
        {
            var bundle = bundleStateCache[CachePrefix + name];
            if(bundle.ForceDebug)
            {
                debugStatusReader.ForceDebug();
            }
            if(bundle.ForceRelease)
            {
                debugStatusReader.ForceRelease();
            }
            return bundle;
        }
    }
}