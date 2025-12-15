using qb.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Threading.Tasks;
using qb.Gif;
using qb.Atlas;
#if !UNITY_WEBGL && !UNITY_WEBGL_API
using System.IO;
#endif
namespace qb.Cache.Network
{
    public class WebTextureCacheHandler : DisposableCacheHandler
    {
        #region enums and classes
        public enum EFormat
        {
            unknown,
            bin,//jpg,png,exr
            gif,
            webp
        }
        public enum EState
        {
            Unloaded,
            Loading,
            Loaded,
            ToBeDisposed,
            Disposed,
            Error
        }
        public enum ECacheSizeTest
        {
            None,
            MatchCacheSize,
            MatchHalfCacheSize
        }
        [Serializable]
        public class MetaData
        {
            public string etag;
            public int[] frames = new int[0];
            public float[] delays = new float[0];
            public bool HasFrames=> frames != null && frames.Length>0;
            public void SetDelays(float[] delays)
            {
                if (HasFrames)
                {
                    int count = frames.Length / 4;
                    if (delays == null || delays.Length != count)
                    {
                        delays = new float[count];
                        for (int i = 0; i < count; i++)
                        {
                            delays[i] = 0.012f;
                        }
                    }
                    this.delays = delays;
                }
                else
                    this.delays = new float [0];
            }
        }
        #endregion

        #region static
        #region static properties
        static string cacheDirectoryName = "WebTextures";

        static object sizeLock = new object();

        static long totalCacheSize;
        public static long TotalCacheSize
        {
            get => totalCacheSize;
            private set
            {
                lock (sizeLock)
                {
                    totalCacheSize = value;
                }
            }
        }
        public static double MemoryFillRate => (double)totalCacheSize / (double)GetMaxCacheSizeInByte();

        public const int _1MB = 100000;
        public static long maxCacheSizeInBytes = _1MB * 5;//1000;
        public static float maxGraphicAmountCacheSize = 0.3f; //30% of the system grapbhics memory size
        public enum EMaxMemoryComputationMode
        {
            Value,
            Percent
        }
        public static EMaxMemoryComputationMode maxMemoryComputationMode = EMaxMemoryComputationMode.Percent;

        public static long GetMaxCacheSizeInByte()
        {
            if (maxMemoryComputationMode == EMaxMemoryComputationMode.Value)
            {
                return maxCacheSizeInBytes;
            }
            return Mathf.FloorToInt(SystemInfo.graphicsMemorySize * Mathf.Clamp(maxGraphicAmountCacheSize, 0.01f, 0.8f));
        }
        /// <summary>
        /// The maximum ceil value of loading allowed at the same time
        /// </summary>
        public const int concurrentLoadingCountCeil = 20;
        /// <summary>
        /// The loading concurrent max count at the same time 
        /// </summary>
        public static int concurrentLoadingMaxCount = 20;

        /// <summary>
        /// The current 
        /// </summary>
        static int concurrentLoadingCount = 0;
        static bool disposeUnusedInProgress;
        static ConcurrentDictionary<string, long> hashFromString = new ConcurrentDictionary<string, long>();
        static ConcurrentDictionary<long, ConcurrentDictionary<long, WebTextureCacheHandler>> handlerDicFromProviderKey = new ConcurrentDictionary<long, ConcurrentDictionary<long, WebTextureCacheHandler>>();
        #endregion

        #region static methods

        public static WebTextureCacheHandler Get(MainThreadAction mainThreadAction, string url, EFormat format = EFormat.unknown, int maxImageCount = 0)
        {
            if (mainThreadAction == null)
                throw new ArgumentException("MainThreadAction null argument");

            if (string.IsNullOrEmpty(url)) return null;
            WebTextureCacheHandler handler = null;

            ConcurrentDictionary<long, WebTextureCacheHandler> webTextureHandlerDic=null;

            (var providerName, var fileName) = ExtractProviderAndFileNameFromUrl(url);
            if(!hashFromString.TryGetValue(providerName,out long providerKey))
            {
                providerKey = GetHash(providerName);
                hashFromString.TryAdd(providerName, providerKey);
                webTextureHandlerDic = new ConcurrentDictionary<long, WebTextureCacheHandler>();
                handlerDicFromProviderKey.TryAdd(providerKey, new ConcurrentDictionary<long, WebTextureCacheHandler>());
            }
            if (!hashFromString.TryGetValue(fileName, out long fileKey))
            {
                fileKey = GetHash(fileName);
                hashFromString.TryAdd(fileName, fileKey);
            }
            if (webTextureHandlerDic == null && !handlerDicFromProviderKey.TryGetValue(providerKey,out webTextureHandlerDic))
            {
                //error !
                return null;
            }
            if(webTextureHandlerDic.TryGetValue(fileKey,out handler))
            {
                return handler;
            }
            else
            {
                hashFromString.TryAdd(fileName,fileKey);
                handler = new WebTextureCacheHandler(mainThreadAction, url, new CacheKeys(providerKey,fileKey), format, maxImageCount);
                webTextureHandlerDic.TryAdd(fileKey, handler);

            }
            
            return handler;
        }

        /// <summary>
        /// Destroy unused textures 
        /// </summary>
        /// <param name="cacheSizeTest">
        /// The cache size test used to stop the clean up
        /// None:               no cache size test, all unused textures will be destroyed
        /// 
        /// MatchCacheSize:     unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes
        /// 
        /// MatchHalfCacheSize: unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes/2
        /// </param>
        /// <returns>The released bytes size</returns>
        public static long DisposeUnusedTextures(ECacheSizeTest cacheSizeTest = ECacheSizeTest.None)
        {
            if (disposeUnusedInProgress) return 0;

            disposeUnusedInProgress = true;
            var total = TotalCacheSize;
            try
            {
                if (cacheSizeTest == ECacheSizeTest.None)
                {
                    foreach(var pdic in handlerDicFromProviderKey.Values)
                        foreach (var entry in pdic)
                        {
                            var handler = entry.Value;
                            if (handler.UseCount == 0)
                            {
                                handler.state = EState.ToBeDisposed;
                            }
                        }
                }
                else
                {
                    var maxSize = GetMaxCacheSizeInByte();
                    long target = cacheSizeTest == ECacheSizeTest.MatchCacheSize ? maxSize : Mathf.RoundToInt(maxCacheSizeInBytes / 2f);
                    long t = total;
                    foreach (var pdic in handlerDicFromProviderKey.Values)
                    {
                        foreach (var entry in pdic)
                        {
                            var handler = entry.Value;
                            if (handler.UseCount == 0 && handler.state == EState.Loaded)
                            {
                                handler.state = EState.ToBeDisposed;
                                t -= handler.TextureWeight;
                                if (t <= maxSize) break;
                            }
                        }
                        if (t <= maxSize) break;
                    }
                }
                foreach (var pdic in handlerDicFromProviderKey.Values)
                    foreach (var entry in pdic)
                    {
                        var handler = entry.Value;
                        if (handler.state == EState.ToBeDisposed)
                            handler.Dispose();
                    }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                disposeUnusedInProgress = false;
            }
            return total - TotalCacheSize;
        }
        

        /// <summary>
        /// Return the device cache directory
        /// </summary>
        /// <param name="cacheSubdirectory">
        /// The optionnal cache sub directory name
        /// </param>
        /// <returns></returns>
        public static string GetCacheDirPath(string cacheSubdirectory = "")
        {
            return string.IsNullOrEmpty(cacheSubdirectory)
                ? $"{Application.persistentDataPath}/{cacheDirectoryName}/"
                : $"{Application.persistentDataPath}/{cacheSubdirectory}/{cacheDirectoryName}/";
        }
        /// <summary>
        /// Remove all invalid owner from cache.
        /// This method can be called to remove null owner 
        /// in case of Release method was not call before 
        /// owner destruction
        /// </summary>
        public static void ClearAllInvalidOwners()
        {
            foreach(var dic in handlerDicFromProviderKey.Values)
                foreach (var entry in dic.Values)
                {
                    entry.ClearInvalidOwners();
                }
        }
        public static bool IsCacheEntryExist(string url, string cacheSubDirectory = "")
        {
            (var providerName, var fileName) = ExtractProviderAndFileNameFromUrl(url);
            if (hashFromString.ContainsKey(providerName) && hashFromString.ContainsKey(fileName))
                return true;
#if !UNITY_WEBGL && !UNITY_WEBGL_API
            else
            {

                var cachePath = GetCacheDirPath(cacheSubDirectory);
                if (!Directory.Exists(cachePath)) return false;

                var key = GetCacheKeys(url);
                var path = GetCacheFilePath(key, cacheSubDirectory, false);
                if (File.Exists(path + $".{EFormat.bin}")) return true;
                if (File.Exists(path + $".{EFormat.gif}")) return true;
            }
#endif
            return false;
        }
       
        static (string,string) ExtractProviderAndFileNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return ("", "");
            
            int startProviderIndex = url.LastIndexOf("://");
            if (startProviderIndex == -1)
                return ("", "");

            startProviderIndex += 3;
            var providerName = url.Substring(startProviderIndex);
            int startFilenameIndex = providerName.LastIndexOf("/");
            if (startFilenameIndex == -1)
                return (providerName, "");
            startFilenameIndex++;
            var fileName = providerName.Substring(startFilenameIndex);
            providerName = providerName.Substring(0, startFilenameIndex);
            return (providerName,fileName);
        }
        static CacheKeys GetCacheKeys(string url)
        {
            if (string.IsNullOrEmpty(url)) return new CacheKeys(0,0);
            /*
            var match = Regex.Match(url, @"path=([a-zA-Z0-9._-]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match.Groups.Count < 2)
                match = Regex.Match(url, @"//([a-zA-Z0-9/._-]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var fileName = (match.Groups.Count < 2) ? match.Groups[0].fValue : match.Groups[1].fValue;
            */
            (var providerName, var fileName) = ExtractProviderAndFileNameFromUrl(url);

            long fileKey = GetHash(fileName);
            long providerKey = GetHash(providerName);

            Debug.Log($"{providerName}=>{providerKey} {fileName}=>{fileKey}");

            return new CacheKeys(providerKey,fileKey);
        }

        static long GetHash(string str)
        {
            using var hasher = MD5.Create();
            var bytes = Encoding.ASCII.GetBytes(str);
            var hBytes = hasher.ComputeHash(bytes);
            return hBytes.Select((q, i) => System.Convert.ToInt64(q * Math.Pow(10, i + 1))).Sum();
        }

        void GenerateAtlasFromGifData(byte[] data)
        {
            atlas = null;
            delays = null;
            if (!GifParser.IsGif(data)) return;

            var images = GifParser.GetImages(data, out var imageWidth, out var imageHeight, maxImageCount);
            int imagesCount = images.Count;
            if (imagesCount > 0)
            {
                delays = new float[imagesCount];
                atlas = new USTextureAtlas(images.Count, imageWidth, imageHeight, AtlasBuildWidth, atlasBuildPadding);
                for (int i = 0; i < imagesCount; i++)
                {
                    var image = images[i];
                    atlas.AddFrame(image.colors);
                    image.colors = null;
                    delays[i] = image.SafeDelaySeconds;
                }
            }
            images.Clear();
        }

#if !UNITY_WEBGL && !UNITY_WEBGL_API


        /// <summary>
        /// Remove the device cache root directory with all saved file if exists 
        /// </summary>
        /// <param name="cacheSubdirectory">The optionnal cache sub directory name</param>
        public static void DeleteAllSavedCache(string cacheSubdirectory = "")
        {
            try
            {
                var cachePath = string.IsNullOrEmpty(cacheSubdirectory)
                    ? $"{Application.persistentDataPath}/{cacheDirectoryName}/"
                    : $"{Application.persistentDataPath}/{cacheSubdirectory}/{cacheDirectoryName}/";

                if (Directory.Exists(cachePath))
                {
                    Directory.Delete(cachePath, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        
        /// <summary>
        /// Delete cached files from url if exists
        /// </summary>
        /// <param name="url">The target file url</param>
        /// <param name="cacheSubdirectory">The optionnal cache sub directory name</param>
        public static void DeleteSavedCache(string url, string cacheSubdirectory = "")
        {
            try
            {
                var key = GetCacheKeys(url);
                if (!IsCacheDirectoryExist(key, cacheSubdirectory))
                    return;
                var path = GetCacheFilePath(key, cacheSubdirectory,false);
                var formats = System.Enum.GetNames(typeof(EFormat));
                foreach (var format in formats)
                {
                    var imagePath = $"{path}.{format}";
                    if (File.Exists(imagePath))
                    {
                        File.Delete(imagePath);
                        break;
                    }
                }
                var etagPath = path + ".etag";
                if (File.Exists(etagPath))
                    File.Delete(etagPath);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }


        public static void DeleteSavedCacheFromProvider(long providerKey,string cacheSubdirectory = "")
        {
            var path = $"{GetCacheDirPath(cacheSubdirectory)}/{providerKey}";
            if (Directory.Exists(path))
            {
                DirectoryInfo di = new DirectoryInfo(path);

                foreach (FileInfo file in di.GetFiles())
                {
                    file.Delete();
                }
                foreach (DirectoryInfo dir in di.GetDirectories())
                {
                    dir.Delete(true);
                }
            }
        }
        static bool TryToReadMetaDataFromCache(CacheKeys key, string cacheSubdirectory, out MetaData metaData)
        {
            metaData = null;
            if (!IsCacheDirectoryExist(key, cacheSubdirectory))
                return false;

            var path = GetCacheFilePath(key, cacheSubdirectory, false);
            var metaPath = path + ".meta";
            if (File.Exists(metaPath))
            {
                try
                {
                    using (var stream = File.Open(metaPath, FileMode.Open))
                    {
                        int[] frames = new int[0];
                        float[] delays = new float[0];
                        using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
                        {
                            var etag = reader.ReadString();
                            var frameValuesCount = reader.ReadInt32();
                            if (frameValuesCount > 0)
                            {
                                frames = new int[frameValuesCount];
                                for (int i = 0; i < frameValuesCount; i++)
                                {
                                    frames[i] = reader.ReadInt32();
                                }
                                int delaysCount = frameValuesCount / 4;
                                delays = new float[delaysCount];
                                for (int i = 0; i < delaysCount; i++)
                                {
                                    delays[i] = reader.ReadSingle();
                                }

                            }
                            metaData = new MetaData { etag = etag, frames = frames,delays = delays };
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            return false;
        }


        static void WriteMetaDataToCache(CacheKeys key,string cacheSubdirectory,MetaData data)
        {
            var path = GetCacheFilePath(key, cacheSubdirectory,true);
            WriteMetaDataToCache(path, data);
        }
        static void WriteMetaDataToCache(string path,MetaData data)
        {
            var metaPath = path + ".meta";
            using (var stream = File.Open(metaPath, FileMode.Create))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
                {
                    writer.Write(data.etag);
                    if (data.HasFrames)
                    {
                        var frames = data.frames;
                        int count = frames.Length;
                        writer.Write(count);
                        for (int i = 0; i < count; i++)
                            writer.Write(frames[i]);
                        var delays = data.delays;
                        if(delays != null)
                        {
                            count = delays.Length;
                            for (int i = 0; i < count; i++)
                                writer.Write(delays[i]);
                        }
                    }
                    else
                    {
                        writer.Write(0);
                    }
                }
            }

        }

        static string GetCacheEtag(CacheKeys key, EFormat format, string cacheSubdirectory)
        {
            var path = GetCacheFilePath(key, cacheSubdirectory,false);
            var infoPath = path + ".etag";
            var dataPath = $"{path}.{format}";
            return File.Exists(infoPath) && File.Exists(dataPath)
                    ? File.ReadAllText(infoPath)
                    : null;
        }
        static bool IsCacheDirectoryExist(CacheKeys key, string cacheSubdirectory) => Directory.Exists($"{GetCacheDirPath(cacheSubdirectory)}/{key.providerKey}");
       
        static string GetCacheFilePath(CacheKeys key, string cacheSubDirectory,bool createDirectoriesIfNeeded)
        {

            var cachePath = GetCacheDirPath(cacheSubDirectory);

            if (!Directory.Exists(cachePath) && createDirectoriesIfNeeded)
                Directory.CreateDirectory(cachePath);
            var finalDirPath = $"{cachePath}{key.providerKey}";
            if (!Directory.Exists(finalDirPath) && createDirectoriesIfNeeded)
            {
                Directory.CreateDirectory(finalDirPath);
#if UNITY_EDITOR
                Debug.Log($"Cache directory created:\n{finalDirPath}");
#endif
            }
            return $"{cachePath}{key}";
        }
        bool LoadGifFromCache(
            CacheKeys key, string cacheSubDirectory,
            int maxImageCount = 0,
            bool verbose = false)
        {
            try
            {
                var path = GetCacheFilePath(key, cacheSubDirectory,false) + $".{EFormat.gif}";
                if (File.Exists(path))
                {
                    GenerateAtlasFromGifData(File.ReadAllBytes(path));
                    if(atlas==null)
                    {
                        state = EState.Error;
                        errorCode = -2;
                        error = $"Gif decode error for url [{url}]";
                        atlas = null;
                        delays = null;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return atlas!=null;
        }
        
       
        static Texture2D LoadUnitySupportedImageFromCache(CacheKeys key, string cacheSubdirectory)
        {
            var texture = new Texture2D(2, 2);
            try
            {
                var path = GetCacheFilePath(key, cacheSubdirectory, false) + $".{EFormat.bin}";
                if (File.Exists(path))
                {
                    byte[] data = File.ReadAllBytes(path);
                    ImageConversion.LoadImage(texture, data);
                    //texture.LoadImage(data, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return texture;
        }


        static void SaveCache(CacheKeys key, EFormat format, string cacheSubdirectory, MetaData metaData, byte[] datas)
        {
            try
            {
                var path = GetCacheFilePath(key, cacheSubdirectory,true);
                WriteMetaDataToCache(path, metaData);
                File.WriteAllBytes($"{path}.{format}", datas);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

        }
#endif
#endregion
#endregion

        #region properties
        public readonly string url;
        public struct CacheKeys
        {
            public CacheKeys(long providerKey, long fileKey)
            {
                this.providerKey = providerKey;
                this.fileKey = fileKey;
            }
            public readonly long providerKey, fileKey;

            public override string ToString()
            {
                return $"{providerKey}/{fileKey}";
            }
            public bool IsValid => providerKey > 0 && fileKey > 0;
        }

        public readonly CacheKeys cacheKeys;

        EFormat format;
        public EFormat Format => format;

        Texture2D texture;

        public Texture2D Texture => texture != null ? texture : atlas!=null?atlas.AtlasTexture:null;


        USTextureAtlas atlas;
        public USTextureAtlas Atlas => atlas;

        public USTextureAtlas.PowerOfTwoWidth AtlasBuildWidth = USTextureAtlas.PowerOfTwoWidth._1024;
        int atlasBuildPadding = 0;
        public int AtlasBuildPadding
        {
            get => atlasBuildPadding;
            set => atlasBuildPadding = Mathf.Clamp(value,0,8);
        }

        float[] delays=new float[0];
        public float[] Delays => metaData!=null?metaData.delays: delays;

        public int Width => texture != null  ? texture.width : atlas!=null?atlas.FrameWidth:0;
        public int Height => texture != null ? texture.height : atlas!=null?atlas.FrameHeight:0;

        public float HorizontalRatio => texture != null  ? (float)texture.width / texture.height 
                     :atlas!=null?atlas.FrameHorizontalRatio: 1;
        public float VerticalRatio => texture != null ? (float)texture.height / texture .width 
                     :atlas!=null?atlas.FrameVerticalRatio: 1;

        int maxImageCount;

        UnityWebRequestAsyncOperation webRequestAsyncOperation;

        static object stateLock = new object();

        EState state = EState.Unloaded;
        public EState State
        {
            get => state;
            set
            {
                lock (stateLock)
                {
                    state = value;
                }
            }
        }

        string error;
        public string Error => error;
        int errorCode = 0;
        public int ErrorCode => errorCode;

        public long TextureWeight
        {
            get
            {

                if (texture != null)
                {
                    return texture.width * texture.height * 4;
                }
                else if(atlas != null)
                {
                    var tex = atlas.AtlasTexture;
                    if(tex != null)
                        return tex.width * tex.height * 4;
                }
                return 0;
            }
        }

        MetaData metaData;

        MainThreadAction mainThreadAction;
        #endregion


        WebTextureCacheHandler(MainThreadAction mainThreadAction, string url, CacheKeys cacheKeys, EFormat format, int maxImageCount = 0)
        {
            if (mainThreadAction == null)
                throw new ArgumentException("MainThreadAction null argument");

            this.mainThreadAction = mainThreadAction;
            this.url = url;
            this.cacheKeys = cacheKeys;
            this.format = format;
            this.maxImageCount = maxImageCount;
        }


        /// <summary>
        /// Load texture from server or device cache with etag management
        /// 
        /// </summary>
        /// 
        /// <param name="owner">
        /// The owner object of the texture.
        /// To drive an usage mechanism each load is binded with an owner.
        /// When there are no more valid owner binded with the handler the managed textures are marked as 
        /// not use and can be disposed from cache with the static method DisposeUnusedTextures
        /// </param>
        /// <param name="onProgress">
        /// Optionnal action<float> called during the loading progression
        /// </param>
        /// <param name="cacheSubdirectory">
        /// The optionnal cache sub directory name, can be used to managed different application version
        /// </param>
        /// <param name="cacheSizeTest">
        /// The test to use if the cache memory size > maxCacheSizeInBytes.
        /// None:               no cache size test nothing was done
        /// 
        /// MatchCacheSize:     unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes
        /// 
        /// MatchHalfCacheSize: unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes/2
        /// </param>
        /// <param name="timeOutInSecond"/>
        /// Loading time out in seconds, set to zero by default for no timeout
        /// </param>
        /// <param name="verbose">
        /// Debug log message flag.
        /// </param>
        public async Task LoadRequest(object owner,
                                     Action<float> onProgress = null,
                                     string cacheSubdirectory = "",
                                     ECacheSizeTest cacheSizeTest = ECacheSizeTest.None,
                                     float timeOutInSecond = 0,
                                     bool verbose = false)
        {
            if (state == EState.Disposed) return;

            lock (ownersLock)
            {
                if (owner == null)
                    owners.RemoveAll(x => x.Equals(null));
                else
                    if (!owners.Contains(owner))
                {
                    owners.Add(owner);
                }
            }

            switch (state)
            {
                default:
                    state = EState.Loading;
                    switch (format)
                    {
                        case EFormat.unknown:
                            await LoadUnkownImageFormat(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                            break;
                        case EFormat.bin:
                            await LoadUnitySupportedImage(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                            break;
                        case EFormat.gif:
                            await LoadGif(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                            break;
                        case EFormat.webp:
                            await LoadWebp(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                            break;
                    }
                    break;
                case EState.Loading:
                    while (state == EState.Loading)
                    {
                        if (webRequestAsyncOperation != null && !webRequestAsyncOperation.isDone)
                            onProgress?.Invoke(webRequestAsyncOperation.progress);
                        await Task.Yield();
                    }
                    break;

            }
            if (state == EState.Error)
            {
                lock (ownersLock)
                {
                    if (owner != null)
                    {
                        owners.Remove(owner);
                    }
                    else
                    {
                        owners.RemoveAll(x => x.Equals(null));
                    }
                }
                if (verbose)
                {
                    Debug.LogWarning(error);
                }
            }
            else if (cacheSizeTest != ECacheSizeTest.None)
            {
                long target = cacheSizeTest == ECacheSizeTest.MatchCacheSize ? GetMaxCacheSizeInByte() : Mathf.RoundToInt(maxCacheSizeInBytes / 2f);
                if (TotalCacheSize > target)
                {
                    DisposeUnusedTextures(cacheSizeTest);
                }
            }

            onProgress?.Invoke(1);
        }

        /// <summary>
        /// Load texture from server or cache with etag management 
        /// </summary>
        /// <param name="owner">
        /// The owner object to bind with the handle.
        /// To drive an usage mechanism each load is binded with an owner.
        /// When there are no more valid owner binded with the handler the managed textures are marked as 
        /// not use and can be disposed from cache with the static method DisposeUnusedTextures        /// </param>
        /// <param name="onCompleted">The action called when textures are loaded</param>
        /// <param name="onProgress">
        /// Optionnal action<float> called during the loading progression
        /// </param>
        /// <param name="cacheSubdirectory">
        /// The optionnal cache sub directory name, can be used to managed different application version
        /// </param>
        /// <param name="cacheSizeTest">
        /// The test to use if the cache memory size > maxCacheSizeInBytes.
        /// None:               no cache size test nothing was done
        /// 
        /// MatchCacheSize:     unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes
        /// 
        /// MatchHalfCacheSize: unused textures will be destroyed from cache until 
        ///                     until the cache memory size become <= to the maxCacheSizeInBytes/2
        /// </param>
        /// <param name="timeOutInSecond"/>
        /// Loading time out in seconds, set to zero by default for no timeout
        /// </param>
        /// <param name="verbose">
        /// Debug log message flag.
        /// </param>
        public async void Load(object owner,
            Action onCompleted,
            Action<float> onProgress = null,
            string cacheSubdirectory = "",
            ECacheSizeTest cacheSizeTest = ECacheSizeTest.None,
            float timeOutInSecond = 0,
            bool verbose = false)
        {
            if (state != EState.Disposed) return;
            await LoadRequest(owner, onProgress, cacheSubdirectory, cacheSizeTest, timeOutInSecond, verbose);
            onCompleted?.Invoke();
        }

        async Task LoadUnkownImageFormat(
            Action<float> onProgress = null,
            string cacheSubdirectory = "",
            float timeOutInSecond = 0,
            bool verbose = false)
        {

#if !UNITY_WEBGL && !UNITY_WEBGL_API
            var cachePath = GetCacheFilePath(cacheKeys, cacheSubdirectory,false);
            if (File.Exists($"{cachePath}.{EFormat.bin}"))
            {
                format = EFormat.bin;
                await LoadUnitySupportedImage(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                return;
            }
            else if (File.Exists($"{cachePath}.{EFormat.gif}"))
            {
                format = EFormat.gif;
                await LoadGif(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                return;
            }
            else if (File.Exists($"{cachePath}.{EFormat.webp}"))
            {
                format = EFormat.webp;
                await LoadWebp(onProgress, cacheSubdirectory, timeOutInSecond, verbose);
                return;
            }
#endif
            try
            {
                int concurrentTargetCount = Mathf.Min(concurrentLoadingCountCeil, concurrentLoadingMaxCount);
                if (concurrentLoadingCount > concurrentTargetCount)
                {
                    while (concurrentLoadingCount > concurrentTargetCount)
                        await Task.Yield();
                }
                concurrentLoadingCount++;

                using (UnityWebRequest uwr = UnityWebRequest.Get(new Uri(url)))
                {
                    if (timeOutInSecond > 0)
                        uwr.timeout = Mathf.RoundToInt(timeOutInSecond * 1000);

                    webRequestAsyncOperation = uwr.SendWebRequest();

                    while (!webRequestAsyncOperation.isDone)
                    {
                        onProgress?.Invoke(webRequestAsyncOperation.progress);
                        await Task.Yield();
                    }


                    if (uwr.result == UnityWebRequest.Result.Success)
                    {
                        var data = uwr.downloadHandler.data;
                        if (data != null)
                        {
                            if (GifParser.IsGif(data))
                            {
                                format = EFormat.gif;
                                var images = GifParser.GetImages(data,out var imageWidth,out var imageHeight,  maxImageCount);
                                int imagesCount = images.Count;
                                if (imagesCount > 0)
                                {
                                    delays = new float[imagesCount];
                                    atlas = new USTextureAtlas(images.Count, imageWidth, imageHeight, AtlasBuildWidth,atlasBuildPadding);
                                    for (int i = 0;i<imagesCount;i++)
                                    {
                                        var image = images[i];
                                        delays[i] = image.SafeDelaySeconds;
                                        atlas.AddFrame(image.colors);
                                    }
                                }
                                else
                                {
                                    state = EState.Error;
                                    errorCode = -2;
                                    error = $"Gif decode error for url [{url}]";
                                    return;
                                }
                            }
                            else
                            {
                                texture = new Texture2D(2, 2);
                                if (ImageConversion.LoadImage(texture, data))
                                {
                                    format = EFormat.bin;
                                    delays = new float[0];
                                }
                                else
                                {
                                    //try to decode webp
                                    format = EFormat.webp;

                                    state = EState.Error;
                                    errorCode = -2;
                                    error = $"Loading image format [{format}] not implemented yet!";
                                    return;
                                }
                            }
                            if(metaData==null)
                                metaData = new MetaData();
                            metaData.etag = uwr.GetResponseHeader("Etag");
                            byte[] fileData;
                            if (atlas != null)
                            {
                                metaData.frames = atlas.Frames;
                                metaData.SetDelays(delays);
                                fileData = atlas.AtlasTexture.EncodeToPNG();
                            }
                            else
                                fileData = uwr.downloadHandler.data;
#if !UNITY_WEBGL && !UNITY_WEBGL_API
                            SaveCache(cacheKeys,EFormat.bin,cacheSubdirectory, metaData,fileData);
#endif
                            state = EState.Loaded;
                            TotalCacheSize += TextureWeight;
                        }
                    }
                    else
                    {
                        state = EState.Error;
                        errorCode = (int)uwr.responseCode;
                        error = uwr.error;
                    }
                }
            }
            catch (Exception ex)
            {
                state = EState.Error;
                errorCode = -3;
                error = ex.Message;
            }
            finally
            {
                concurrentLoadingCount--;
            }

            onProgress?.Invoke(1);
        }

        async Task LoadUnitySupportedImage(
            Action<float> onProgress = null,
            string cacheSubdirectory = "",
            float timeOutInSecond = 0,
            bool verbose = false)
        {
            try
            {
                int concurrentTargetCount = Mathf.Min(concurrentLoadingCountCeil, concurrentLoadingMaxCount);
                if (concurrentLoadingCount > concurrentTargetCount)
                {
                    while (concurrentLoadingCount > concurrentTargetCount)
                        await Task.Yield();
                }
                concurrentLoadingCount++;
                using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(new Uri(url)))
                {
                    this.texture = null;
                    if (timeOutInSecond > 0)
                        uwr.timeout = Mathf.RoundToInt(timeOutInSecond * 1000);

#if !UNITY_WEBGL && !UNITY_WEBGL_API

                    if(TryToReadMetaDataFromCache(cacheKeys, cacheSubdirectory,out var data))
                    {
                        metaData = data;
                        if (!string.IsNullOrEmpty(metaData.etag))
                        {
                            uwr.SetRequestHeader("If-None-Match", metaData.etag);
                        }
                        else
                        {
                            var tex = LoadUnitySupportedImageFromCache(cacheKeys, cacheSubdirectory);
                            if (metaData.HasFrames)
                            {
                                atlas = new USTextureAtlas(tex, metaData.frames);
                                delays = metaData.delays;
                            }
                            else
                            {
                                texture = tex;
                            }
                        }
                    }
#endif
                    if (texture == null)
                    {
                        webRequestAsyncOperation = uwr.SendWebRequest();

                        while (!webRequestAsyncOperation.isDone)
                        {
                            onProgress?.Invoke(webRequestAsyncOperation.progress);
                            await Task.Yield();
                        }
#if !UNITY_WEBGL && !UNITY_WEBGL_API
                        if (uwr.responseCode == 304)
                        {
                            var tex = LoadUnitySupportedImageFromCache(cacheKeys, cacheSubdirectory);
                            if(metaData != null)
                            {
                                if (metaData.HasFrames)
                                {
                                    atlas = new USTextureAtlas(tex, metaData.frames);
                                    delays = metaData.delays;
                                }
                                else
                                {
                                    texture = tex;
                                }
                            }
                        }
                        else
#endif
                        {
                            if (uwr.result == UnityWebRequest.Result.Success)
                            {
                                if (metaData == null)
                                    metaData = new MetaData();

                                metaData.etag = uwr.GetResponseHeader("Etag");
                                var tex = DownloadHandlerTexture.GetContent(uwr);
                                if (metaData.HasFrames)
                                {
                                    if (metaData.HasFrames)
                                    {
                                        atlas = new USTextureAtlas(tex, metaData.frames);
                                        metaData.SetDelays(delays);
                                    }
                                    else
                                    {
                                        texture = tex;
                                    }
                                }
#if !UNITY_WEBGL && !UNITY_WEBGL_API
                                SaveCache(cacheKeys, EFormat.bin, cacheSubdirectory, metaData, uwr.downloadHandler.data);
#endif
                            }
                            else
                            {
                                state = EState.Error;
                                errorCode = (int)uwr.responseCode;
                                error = uwr.error;
                            }
                        }
                    }
                    if(this.Texture == null && uwr.result == UnityWebRequest.Result.Success)
                    {
                        texture = DownloadHandlerTexture.GetContent(uwr);
                    }
                    if (this.Texture != null)
                    {
                        state = EState.Loaded;
                        delays = metaData!=null?metaData.delays: new float[0];
                        TotalCacheSize += TextureWeight;
                    }
                    else
                    {
                        state = EState.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                state = EState.Error;
                errorCode = -3;
                error = ex.Message;
            }
            finally
            {
                concurrentLoadingCount--;
            }
        }
        async Task LoadWebp(
            Action<float> onProgress = null,
            string cacheSubdirectory = "",
            float timeOutInSecond = 0,
            bool verbose = false)
        {
            state = EState.Error;
            errorCode = -2;
            error = $"Loading image format [{format}] not implemented yet!";
            await Task.Yield();
        }

        async Task LoadGif(
            Action<float> onProgress = null,
            string cacheSubdirectory = "",
            float timeOutInSecond = 0,
            bool verbose = false)
        {
            int concurrentTargetCount = Mathf.Min(concurrentLoadingCountCeil, concurrentLoadingMaxCount);
            if (concurrentLoadingCount > concurrentTargetCount)
            {
                while (concurrentLoadingCount > concurrentTargetCount)
                    await Task.Yield();
            }
            try
            {
                concurrentLoadingCount++;
                using (UnityWebRequest uwr = UnityWebRequest.Get(new Uri(url)))
                {
                    if (timeOutInSecond > 0)
                        uwr.timeout = Mathf.RoundToInt(timeOutInSecond * 1000);

#if !UNITY_WEBGL && !UNITY_WEBGL_API

                    if (TryToReadMetaDataFromCache(cacheKeys, cacheSubdirectory, out var data))
                    {
                        metaData = data;
                        if (!string.IsNullOrEmpty(metaData.etag))
                        {
                            uwr.SetRequestHeader("If-None-Match", metaData.etag);
                        }
                        else
                        {
                            LoadUnitySupportedImageFromCache(cacheKeys, cacheSubdirectory);
                        }
                    }
#endif

                    if (atlas == null)
                    {
                        webRequestAsyncOperation = uwr.SendWebRequest();

                        while (!webRequestAsyncOperation.isDone)
                        {
                            onProgress?.Invoke(webRequestAsyncOperation.progress);
                            await Task.Yield();
                        }

#if !UNITY_WEBGL && !UNITY_WEBGL_API

                        if (uwr.responseCode == 304)
                        {
                            LoadUnitySupportedImageFromCache(cacheKeys, cacheSubdirectory);
                        }
                        else
#endif
                        {
                            if (uwr.result == UnityWebRequest.Result.Success)
                            {
                                if (metaData == null)
                                    metaData = new MetaData();
                                metaData.etag = uwr.GetResponseHeader("Etag");
                                GenerateAtlasFromGifData(uwr.downloadHandler.data);
                                if (atlas != null)
                                {
                                    metaData.frames = atlas.Frames;
                                    metaData.SetDelays(delays);
#if !UNITY_WEBGL && !UNITY_WEBGL_API

                                    SaveCache(cacheKeys, EFormat.bin, cacheSubdirectory, metaData, atlas.AtlasTexture.EncodeToPNG());
#endif
                                }
                                else
                                {
                                    state = EState.Error;
                                    errorCode = -2;
                                    error = $"Image format from [{url}] is not {format}";
                                }
                            }
                            else
                            {
                                state = EState.Error;
                                errorCode = (int)uwr.responseCode;
                                error = uwr.error;
                            }
                        }
                    }
                    if (atlas != null)
                    {
                        state = EState.Loaded;
                        TotalCacheSize += TextureWeight;
                        onProgress?.Invoke(1);
                    }
                }
            }
            catch (Exception ex)
            {
                state = EState.Error;
                errorCode = -3;
                error = ex.Message;
            }
            finally
            {
                concurrentLoadingCount--;
            }
        }

        protected async override void Dispose()
        {
            if (state != EState.Loaded && state != EState.ToBeDisposed)
            {
                Debug.LogWarning($"Can't dispose an handle on state [{state}]");
                return;
            }
            if(handlerDicFromProviderKey.TryGetValue(cacheKeys.providerKey,out var dic))
            {
                if (dic.TryRemove(cacheKeys.fileKey,out var handler))
                {
                    hashFromString.TryRemove(url, out var key);
                    state = EState.Disposed;

                    var w = TextureWeight;
                    if (w > 0)
                    {
                        TotalCacheSize -= w;
                        await Task.Yield();
                        mainThreadAction?.Do(() =>
                        {
                            if (texture != null)
                            {
                                Texture2D.Destroy(texture);
                                texture = null;
                            }
                            else if (atlas != null)
                            {
                                var t = atlas.AtlasTexture;
                                if (t)
                                    Texture2D.Destroy(t);
                                atlas = null;
                            }
                            delays = null;
                        });
                    }

                }
                else
                {
                    Debug.LogWarning($"[{url}]: Try to remove texture failed!");
                }
            }
        }
    }
}
