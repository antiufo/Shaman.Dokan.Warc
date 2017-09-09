using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Utf8;
using System.Threading.Tasks;
using DokanNet;
using Shaman.Dom;
using Shaman.Runtime;
using Shaman.Scraping;
using Shaman;
using Newtonsoft.Json.Linq;

namespace Shaman.Dokan
{
    class WarcFs : ReadOnlyFs
    {


        private object TagVirtual = new object();
        private Dictionary<string, FsNode<WarcItem>> urlToFsNode = new Dictionary<string, FsNode<WarcItem>>();
        public WarcFs(string cdx)
        {
            this.cdx = cdx;
            byte[] fileNameBytes = null;
            string fileNameString = null;
            var folder = Path.GetDirectoryName(cdx);

            this.Root = CreateTree<WarcItem>(WarcCdxItemRaw.Read(cdx).Select(x =>
            {
                var response = x.ResponseCode;
                if (response.Length != 0)
                {
                    var responseCode = Utf8Utils.ParseInt32(response);
                    if (responseCode < 200 || responseCode >= 300) return null;
                }
                return x.ToWarcItem(folder, ref fileNameBytes, ref fileNameString);
            }).Where(x => x != null), x =>
            {

                var url = new Uri(x.Url);

                var keep = -1;
                if (url.AbsolutePath.StartsWith("/w/images/")) keep = 2;
                else if (url.AbsolutePath.StartsWith("/wiki/")) keep = 1;
                else if (url.Host.EndsWith(".fbcdn.net")) keep = 0;
                else if (url.Host.EndsWith(".media.tumblr.com")) keep = 0;
                else if (url.Host.EndsWith(".bp.blogspot.com")) keep = 0;
                else if (url.Host.EndsWith(".reddit.com") && url.AbsolutePath.Contains("/comments/")) keep = 3;
                else if (url.Host.EndsWith(".staticflickr.com")) keep = 0;
                else if (url.Host.EndsWith(".giphy.com") && url.Host.Contains("media")) keep = 0;
                var path = WebsiteScraper.GetPathInternal(null, url, x.ContentType, keep);
                path = path.Replace('/', '\\');

                if (path.Length > 150)
                {
                    var z = path.IndexOf('‽');
                    if (z != -1)
                    {
                        path = path.Substring(0, z) + "‽{" + Math.Abs((long)path.GetHashCode()) + "}" + Path.GetExtension(path);
                    }

                }

                if (url.IsHostedOn("facebook.com") && url.AbsolutePath.StartsWith("/pages_reaction_units/"))
                {
                    path = path.TrimEnd(".js");
                    path += ".html";
                }

                return path;

            }, null, x => 
            {
                x.Tag = TagVirtual;
                if (x.Info != null)
                    urlToFsNode[x.Info.Url] = x;
            });

            FsNode<WarcItem> rawRoot = null;
            rawRoot = new FsNode<WarcItem>() { Name = "_raw", GetChildrenDelegate = CreateGetChildrenDelegate(this.Root) };
            Func<List<FsNode<WarcItem>>> CreateGetChildrenDelegate(FsNode<WarcItem> reference)
            {
                if (reference.Children == null) return () => null;
                return new Func<List<FsNode<WarcItem>>>(() =>
                {
                    return reference.Children.Where(x => x != rawRoot).Select(x =>
                    {
                        var k = new FsNode<WarcItem>()
                        {
                            Info = x.Info,
                            Name = x.Name,
                            GetChildrenDelegate = CreateGetChildrenDelegate(x),
                            Tag = null,
                            FullName = x.FullName != null ? "_raw\\" + x.FullName : null
                        };
                        return k;
                    }).ToList();
                });
            }

            this.Root.Children.Add(rawRoot);


            cache = new MemoryStreamCache<FsNode<WarcItem>>((item, dest) =>
            {
                if (item.Tag == TagVirtual)
                {
                    var ct = item.Info.ContentType;
                    if (ct != null && ct.Contains("/html") || item.Info.Url.Contains("facebook.com/pages_reaction_units/"))
                    {
                        HtmlNode doc;
                        var pagePath = item.FullName;
                        if (item.Info.Url.Contains("/pages_reaction_units/"))
                        {
                            var jsontext = item.Info.ReadText();
                            var idx = jsontext.IndexOf('{');
                            var json = (JObject)HttpUtils.ReadJsonToken(jsontext, idx);
                            doc = new HtmlDocument("<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>").DocumentNode;
                            doc.OwnerDocument.SetPageUrl(item.Info.Url.AsUri());
                            var body = doc.Descendants("body").First();

                            foreach (var domop in (JArray)json["domops"])
                            {
                                var html = ((JArray)domop).First(x => x is JObject)["__html"].Value<string>();
                                body.AppendChild(html.AsHtmlNode());
                            }

     
                        }
                        else
                        {
                            doc = item.Info.ReadHtml();
                        }
                        ProcessHtml(ref doc, pagePath);
                        var simpleStyle = doc.OwnerDocument.CreateElement("link");
                        simpleStyle.SetAttributeValue("rel", "stylesheet");
                        simpleStyle.SetAttributeValue("href", @"file:///C:\Users\Andrea\Desktop\facebook-simple-css.css");
                        (doc.FindSingle("head") ?? doc).AppendChild(simpleStyle);
                        using (var sw = new StreamWriter(dest, Encoding.UTF8, 16 * 1024, true))
                        {
                            doc.WriteTo(sw);
                        }
                        return;
                    }
                }

                using (var k = item.Info.OpenStream())
                {
                    k.CopyTo(dest);
                }

            });
        }

        private void ProcessHtml(ref HtmlNode doc, string pagePath)
        {
            var pageUrl = doc.OwnerDocument.PageUrl;
            if (pageUrl.Host == "www.facebook.com" && pageUrl.AbsolutePath.StartsWith("/ajax/pagelet/"))
            {
                var olddoc = doc;
                var z = doc.FindAll(@"script:json-token('§respond\\(\\d+\\,') > payload > content > *:reparse-html");
                doc = new HtmlDocument(@"<!doctype html><head><meta charset=""utf-8""></head><body></body></html>").DocumentNode;
                doc.OwnerDocument.PageUrl = olddoc.OwnerDocument.PageUrl;
                var body = doc.FindSingle("body");
                foreach (var item in z)
                {
                    body.AppendChild(item);
                }
            }

            var baseUrl = doc.OwnerDocument.BaseUrl;
            
            foreach (var desc in doc.DescendantsAndSelf())
            {
                if (desc.TagName == "script")
                {
                    desc.SetAttributeValue("src", null);
                    if (false && desc.HasChildNodes)
                    {
                        foreach (var k in desc.ChildNodes.ToList())
                        {
                            desc.ReplaceChild(k, doc.OwnerDocument.CreateTextNode(string.Empty));
                        }
                    }
                }
                if (desc.TagName == "base")
                {
                    desc.Attributes.Remove("href");
                }
                if (desc.TagName == "meta")
                {
                    desc.Attributes.Remove("charset");
                }
                if (desc.HasAttributes)
                {
                    foreach (var attr in desc.Attributes)
                    {
                        var name = attr.Name;
                        if (name == "href" || name == "src")
                        {
                            desc.SetAttributeValue(attr.Name, MakeRelativeFsUrl(baseUrl, attr.Value, pagePath));
                        }
                        else if (name.StartsWith("on"))
                        {
                            desc.SetAttributeValue(name, string.Empty);
                        }
                    }
                    if (desc.TagName == "img")
                    {
                        var v = desc.TryGetImageUrl();
                        if (v != null)
                        {
                            desc.SetAttributeValue("src", MakeRelativeFsUrl(baseUrl, v.AbsoluteUri, pagePath));
                        }
                    }
                }
            }
            var head = doc.Descendants("head").FirstOrDefault();
            var metaCharsetUtf8 = doc.OwnerDocument.CreateElement("meta");
            metaCharsetUtf8.SetAttributeValue("charset", "utf-8");
            if (head != null) head.PrependChild(metaCharsetUtf8);
            else
            {
                var firstElement = doc.ChildNodes.FirstOrDefault(x => x.NodeType == Shaman.Dom.HtmlNodeType.Element);
                if (firstElement != null)
                {
                    doc.InsertBefore(metaCharsetUtf8, firstElement);
                }
            }
        }

        private string MakeRelativeFsUrl(Uri baseUrl, string value, string pagePath)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.StartsWith("#")) return value;
            if (value.Trim().StartsWith("javascript:")) return "javascript:void(0)";
            if (value.StartsWith("//")) value = "http:" + value;
            var z = value.IndexOf(':');
            var q = value.IndexOf('?');
            var s = value.IndexOf('/');
            if (q == -1) q = int.MaxValue;
            if (s == -1) s = int.MaxValue;
            if (z == -1) z = int.MaxValue;
            var min = Math.Min(z, Math.Min(q, s));
            if (z != int.MaxValue && z == min)
            {
                if (!value.StartsWith("http://") && !value.StartsWith("https://")) return value;
            }
            var abs = new Uri(baseUrl, value);
            if (!urlToFsNode.TryGetValue(abs.AbsoluteUri, out var fsnode))
            {
                if (abs.IsHostedOnAndPathStartsWith("facebook.com", "l.php"))
                    return abs.GetQueryParameter("u");
                return abs.AbsoluteUri;
            }

            var rel = WebsiteScraper.GetRelativePath("Z:\\" + pagePath, "Z:\\" + fsnode.FullName);
            return rel;
        }

        private FsNode<WarcItem> Root;
        private string cdx;
        private MemoryStreamCache<FsNode<WarcItem>> cache;

        public override string SimpleMountName => "Warc-" + cdx;

        public override NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {

            if (IsBadName(fileName)) return NtStatus.ObjectNameInvalid;
            if ((access & ModificationAttributes) != 0) return NtStatus.DiskFull;

            var item = GetFile(fileName);
            if (item == null) return DokanResult.FileNotFound;
            if (item.Info != null)
            {
                if ((access & DokanNet.FileAccess.ReadData) != 0)
                {
                    Console.WriteLine("ReadData: " + fileName);


                    if (item.Info.CompressedLength > 32 * 1024 * 1024 && item.Tag == null)
                    {
                        info.Context = new UnseekableStreamWrapper(item.Info.OpenStream());
                    }
                    else
                    {
                        info.Context = cache.OpenStream(item, null);
                    }

                    return NtStatus.Success;
                }
                return NtStatus.Success;
            }
            else
            {
                info.IsDirectory = true;
                return NtStatus.Success;
            }
        }

        public override NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, DokanFileInfo info)
        {
            var item = GetFile(fileName);
            if (item == null)
            {
                fileInfo = default(FileInformation);
                return DokanResult.FileNotFound;
            }
            fileInfo = GetFileInformation(item, true);
            return NtStatus.Success;
        }

        public override NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, DokanFileInfo info)
        {
            fileSystemName = volumeLabel = "WARC";
            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.ReadOnlyVolume | FileSystemFeatures.VolumeIsCompressed | FileSystemFeatures.UnicodeOnDisk;
            return NtStatus.Success;
        }

        private FsNode<WarcItem> GetFile(string fileName)
        {
            return GetNode(Root, fileName);
        }


        protected override IList<FileInformation> FindFilesHelper(string fileName, string searchPattern)
        {
            var item = GetFile(fileName);
            if (item == null) return null;

            if (item.Info == null)
            {
                if (item.Children == null) return new FileInformation[] { };
                var matcher = GetMatcher(searchPattern);
                return item.Children.Where(x => matcher(x.Name)).Select(x => GetFileInformation(x, false)).ToList();
            }
            return null;
        }

        private FileInformation GetFileInformation(FsNode<WarcItem> item, bool precise)
        {
            var date = item.Info?.LastModified ?? item.Info?.Date;

            long len = 0;
            if (item.Info != null)
            {
                var l = cache.TryGetLength(item);
                if (l != null) len = l.Value;
                else
                {
                    if (item.Tag == TagVirtual)
                    {
                        if (precise) len = cache.GetLength(item);
                        else
                        {
                            if (item.Info.PayloadLength != -1) len = item.Info.PayloadLength;
                            else len = 9999;
                        }
                    }
                    else
                    {
                        if (item.Info.PayloadLength != -1) len = item.Info.PayloadLength;
                        else
                        {
                            if (precise) len = cache.GetLength(item);
                            else len = 9999;
                        }
                    }
                }


            }


            return new FileInformation()
            {
                Attributes = item.Info != null ? FileAttributes.Archive : FileAttributes.Directory,
                CreationTime = date,
                FileName = item.Name,
                LastAccessTime = date,
                LastWriteTime = date,
                Length = len
            };
        }

        public override void Cleanup(string fileName, DokanFileInfo info)
        {
        }
    }
}
