using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlueClipboard
{
    [DataContract] internal sealed class Release
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public ReleaseAsset[] Assets { get; set; }
    }
    [DataContract] internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        [DataMember(Name = "digest")] public string Digest { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }
    internal sealed class AvailableUpdate
    {
        public Version Version;
        public ReleaseAsset Asset;
    }
    internal sealed class UpdateService : IDisposable
    {
        private readonly HttpClient client;
        public UpdateService() : this(new HttpClientHandler { AllowAutoRedirect = true }) { }
        internal UpdateService(HttpMessageHandler handler)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueClipboard/" + AppInfo.VersionText);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        public async Task<AvailableUpdate> Check(CancellationToken cancellation)
        {
            using (var response = await client.GetAsync("https://api.github.com/repos/" + AppInfo.Repository + "/releases/latest", cancellation))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Опубликованных релизов пока нет. Проверка заработает после публикации на GitHub.");
                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) throw new InvalidOperationException("GitHub временно ограничил запросы. Попробуйте позже.");
                response.EnsureSuccessStatusCode();
                return Parse(await response.Content.ReadAsStringAsync(), new Version(AppInfo.VersionText));
            }
        }
        internal static AvailableUpdate Parse(string json, Version current)
        {
            Release release;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
            Version version;
            if (release == null || release.Draft || release.Prerelease) return null;
            string tag = release.Tag ?? "";
            string number = tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : tag;
            if (!Version.TryParse(number, out version) || version.Build < 0 || version.Revision > 0 || version <= current) return null;
            var asset = (release.Assets ?? new ReleaseAsset[0]).SingleOrDefault(a => a.Name == AppInfo.AssetName);
            if (asset == null) throw new InvalidDataException("В релизе нет установщика для Windows x64.");
            string expected = "https://github.com/" + AppInfo.Repository + "/releases/download/" + tag + "/" + AppInfo.AssetName;
            if (!String.Equals(asset.Url, expected, StringComparison.Ordinal) || !ValidDigest(asset.Digest) || asset.Size <= 0 || asset.Size > 100 * 1024 * 1024)
                throw new InvalidDataException("Метаданные обновления не прошли проверку. Установка отменена.");
            return new AvailableUpdate { Version = version, Asset = asset };
        }
        internal static bool ValidDigest(string digest)
        {
            return digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == 71 && digest.Substring(7).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }
        internal static bool Verify(string path, string digest, long expectedSize)
        {
            if (!ValidDigest(digest) || new FileInfo(path).Length != expectedSize) return false;
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                return String.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), digest.Substring(7), StringComparison.OrdinalIgnoreCase);
        }
        public async Task<string> Download(AvailableUpdate update, string root, IProgress<int> progress, CancellationToken cancellation)
        {
            string directory = Path.Combine(root, "updates", update.Version.ToString(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string partial = Path.Combine(directory, AppInfo.AssetName + ".partial");
            string complete = Path.Combine(directory, AppInfo.AssetName);
            try
            {
                using (var response = await client.GetAsync(update.Asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage.RequestUri.Scheme != "https") throw new InvalidDataException("Небезопасное перенаправление загрузки.");
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920]; long total = 0; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) != 0)
                        {
                            total += read;
                            if (total > update.Asset.Size) throw new InvalidDataException("Размер обновления не совпадает с ожидаемым.");
                            await output.WriteAsync(buffer, 0, read, cancellation);
                            if (progress != null) progress.Report((int)(100 * total / update.Asset.Size));
                        }
                    }
                }
                if (!Verify(partial, update.Asset.Digest, update.Asset.Size)) throw new InvalidDataException("Контрольная сумма обновления не совпала. Установка отменена.");
                File.Move(partial, complete); return complete;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        public void Dispose() { client.Dispose(); }
    }
}
