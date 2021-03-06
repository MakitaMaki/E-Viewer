﻿using ExClient.Api;
using ExClient.Galleries.Commenting;
using ExClient.Galleries.Metadata;
using ExClient.Galleries.Rating;
using ExClient.Internal;
using ExClient.Models;
using ExClient.Tagging;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Opportunity.Helpers.Universal.AsyncHelpers;
using Opportunity.MvvmUniverse.Collections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using static System.Runtime.InteropServices.WindowsRuntime.AsyncInfo;

namespace ExClient.Galleries
{
    [JsonObject]
    [System.Diagnostics.DebuggerDisplay(@"\{ID = {ID} Count = {Count}\}")]
    public class Gallery : FixedLoadingList<GalleryImage>
    {
        public static IAsyncOperation<Gallery> TryLoadGalleryAsync(long galleryId)
        {
            return Task.Run(async () =>
            {
                using (var db = new GalleryDb())
                {
                    db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                    var cm = db.SavedSet.SingleOrDefault(c => c.GalleryId == galleryId);
                    var gm = db.GallerySet.SingleOrDefault(g => g.GalleryModelId == galleryId);
                    if (gm is null)
                    {
                        return null;
                    }

                    var r = (cm is null) ?
                         new Gallery(gm) :
                         new SavedGallery(gm);
                    await r.InitAsync();
                    return r;
                }
            }).AsAsyncOperation();
        }

        public static IAsyncOperation<IList<Gallery>> FetchGalleriesAsync(IReadOnlyList<GalleryInfo> galleryInfo)
        {
            if (galleryInfo is null)
                throw new ArgumentNullException(nameof(galleryInfo));
            if (galleryInfo.Count <= 0)
                return AsyncOperation<IList<Gallery>>.CreateCompleted(Array.Empty<Gallery>());

            if (galleryInfo.Count <= 25)
            {
                return Run<IList<Gallery>>(async token =>
                {
                    var re = await new GalleryDataRequest(galleryInfo, 0, galleryInfo.Count).GetResponseAsync();
                    var data = re.GalleryMetaData;
                    data.ForEach(async g => await g.InitAsync());
                    return data;
                });
            }
            else
            {
                return Run<IList<Gallery>>(async token =>
                {
                    var result = new Gallery[galleryInfo.Count];
                    var pageCount = MathHelper.GetPageCount(galleryInfo.Count, 25);
                    for (var i = 0; i < pageCount; i++)
                    {
                        var pageSize = MathHelper.GetSizeOfPage(galleryInfo.Count, 25, i);
                        var startIndex = MathHelper.GetStartIndexOfPage(25, i);
                        var re = await new GalleryDataRequest(galleryInfo, startIndex, pageSize).GetResponseAsync();
                        var data = re.GalleryMetaData;
                        data.ForEach(async g => await g.InitAsync());
                        data.CopyTo(result, startIndex);
                    }
                    return result;
                });
            }
        }

        private static readonly IReadOnlyDictionary<string, Category> categoriesForRestApi = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["Doujinshi"] = Category.Doujinshi,
            ["Manga"] = Category.Manga,
            ["Artist CG Sets"] = Category.ArtistCG,
            ["Game CG Sets"] = Category.GameCG,
            ["Western"] = Category.Western,
            ["Image Sets"] = Category.ImageSet,
            ["Non-H"] = Category.NonH,
            ["Cosplay"] = Category.Cosplay,
            ["Asian Porn"] = Category.AsianPorn,
            ["Misc"] = Category.Misc
        };

        public virtual IAsyncActionWithProgress<SaveGalleryProgress> SaveAsync(ConnectionStrategy strategy)
        {
            return Run<SaveGalleryProgress>((token, progress) => Task.Run(async () =>
            {
                await Task.Yield();
                progress.Report(new SaveGalleryProgress(-1, this.Count));
                var loadOP = LoadItemsAsync(0, this.Count);
                token.Register(loadOP.Cancel);
                await loadOP;
                token.ThrowIfCancellationRequested();
                var loadedCount = 0;
                progress.Report(new SaveGalleryProgress(loadedCount, this.Count));

                using (var semaphore = new SemaphoreSlim(10, 10))
                {
                    var loadTasks = this.Select(image => Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            var firstFailed = false;
                            try
                            {
                                var firstChance = image.LoadImageAsync(false, strategy, true);
                                var firstTask = firstChance.AsTask(token);
                                var c = await Task.WhenAny(Task.Delay(30_000), firstTask);
                                if (c != firstTask)
                                {
                                    firstFailed = true;
                                    firstChance.Cancel();
                                }
                            }
                            catch (Exception)
                            {
                                firstFailed = true;
                            }
                            if (firstFailed)
                            {
                                token.ThrowIfCancellationRequested();
                                await image.LoadImageAsync(true, strategy, true).AsTask(token);
                            }
                            progress.Report(new SaveGalleryProgress(Interlocked.Increment(ref loadedCount), this.Count));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    })).ToArray();
                    await Task.WhenAll(loadTasks).ConfigureAwait(false);
                }

                using (var db = new GalleryDb())
                {
                    var gid = this.ID;
                    var myModel = db.SavedSet.SingleOrDefault(model => model.GalleryId == gid);
                    if (myModel is null)
                    {
                        db.SavedSet.Add(new SavedGalleryModel().Update(this));
                    }
                    else
                    {
                        myModel.Update(this);
                    }
                    await db.SaveChangesAsync();
                }
            }));
        }

        private Gallery(long id, ulong token, int recordCount)
            : base(recordCount)
        {
            this.ID = id;
            this.Token = token;
            this.Rating = new RatingStatus(this);
            this.GalleryUri = new GalleryInfo(id, token).Uri;
            if (Client.Current.Settings.RawSettings.TryGetValue("tr", out var trv))
            {
                switch (trv)
                {
                case "1": this.pageSize = 50; break;
                case "2": this.pageSize = 100; break;
                case "3": this.pageSize = 200; break;
                default: this.pageSize = 20; break;
                }
            }
        }

        internal Gallery(GalleryModel model)
            : this(model.GalleryModelId, model.Token, model.RecordCount)
        {
            this.Available = model.Available;
            this.Title = model.Title;
            this.TitleJpn = model.TitleJpn;
            this.Category = model.Category;
            this.Uploader = model.Uploader;
            this.Posted = model.Posted;
            this.FileSize = model.FileSize;
            this.Expunged = model.Expunged;
            this.Rating.AverageScore = model.Rating;
            this.Tags = new TagCollection(this, JsonConvert.DeserializeObject<IList<string>>(model.Tags).Select(t => Tag.Parse(t)));
            this.ThumbUri = new Uri(model.ThumbUri);
        }

        [JsonConstructor]
        internal Gallery(
            long gid,
            string error = null,
            string token = "0",
            string title = null,
            string title_jpn = null,
            string category = null,
            string thumb = null,
            string uploader = null,
            string posted = null,
            string filecount = null,
            long filesize = 0,
            bool expunged = true,
            string rating = null,
            string torrentcount = null,
            string[] tags = null)
            : this(gid, token?.ToToken() ?? 0, int.Parse(filecount, NumberStyles.Integer, CultureInfo.InvariantCulture))
        {
            if (error != null)
            {
                throw new Exception(error);
            }
            this.Available = !expunged;
            this.Title = HtmlEntity.DeEntitize(title);
            this.TitleJpn = HtmlEntity.DeEntitize(title_jpn);
            if (!categoriesForRestApi.TryGetValue(category, out var ca))
            {
                ca = Category.Unspecified;
            }

            this.Category = ca;
            this.Uploader = HtmlEntity.DeEntitize(uploader);
            this.Posted = DateTimeOffset.FromUnixTimeSeconds(long.Parse(posted, NumberStyles.Integer, CultureInfo.InvariantCulture));
            this.FileSize = filesize;
            this.Expunged = expunged;
            this.Rating.AverageScore = double.Parse(rating, NumberStyles.Number, CultureInfo.InvariantCulture);
            this.TorrentCount = int.Parse(torrentcount, NumberStyles.Integer, CultureInfo.InvariantCulture);
            this.Tags = new TagCollection(this, tags.Select(tag => Tag.Parse(tag)));
            this.ThumbUri = ThumbClient.FormatThumbUri(thumb);
        }

        protected IAsyncAction InitAsync()
        {
            return InitOverrideAsync();
        }

        protected virtual IAsyncAction InitOverrideAsync()
        {
            return Task.Run(() =>
            {
                using (var db = new GalleryDb())
                {
                    var gid = this.ID;
                    var myModel = db.GallerySet.SingleOrDefault(model => model.GalleryModelId == gid);
                    if (myModel is null)
                        db.GallerySet.Add(new GalleryModel().Update(this));
                    else
                        myModel.Update(this);
                    db.SaveChanges();
                }
            }).AsAsyncAction();
        }

        protected override GalleryImage CreatePlaceholder(int index)
            => new GalleryImage(this, index + 1);

        protected virtual IAsyncOperation<ImageSource> GetThumbAsync()
        {
            return Run<ImageSource>(async token =>
            {
                await CoreApplication.MainView.Dispatcher.Yield();
                var image = new BitmapImage();
                if (!await ThumbClient.FetchThumbAsync(this.ThumbUri, image))
                    return null;
                return image;
            });
        }

        public Uri GalleryUri { get; }

        #region MetaData

        public long ID { get; }

        public bool Available { get; protected set; }

        public ulong Token { get; }

        public string Title { get; protected set; }

        public string TitleJpn { get; protected set; }

        public Category Category { get; protected set; }

        private readonly WeakReference<ImageSource> thumbImage = new WeakReference<ImageSource>(null);
        public ImageSource Thumb
        {
            get
            {
                if (this.thumbImage.TryGetTarget(out var img))
                    return img;
                var load = GetThumbAsync();
                load.Completed = (asyncInfo, asyncStatus) =>
                {
                    try
                    {
                        if (asyncStatus != AsyncStatus.Completed)
                            return;
                        var r = asyncInfo.GetResults();
                        if (r is null)
                            return;
                        this.thumbImage.SetTarget(r);
                        OnPropertyChanged(nameof(Thumb));
                    }
                    finally
                    {
                        asyncInfo.Close();
                    }
                };
                return GalleryImage.DefaultThumb;
            }
        }

        public Uri ThumbUri { get; }

        public string Uploader { get; }

        public DateTimeOffset Posted { get; }

        public long FileSize { get; }

        private int pageSize;
        public int PageSize { get => this.pageSize; set => Set(ref this.pageSize, value); }

        public bool Expunged { get; }

        public RatingStatus Rating { get; }

        public int TorrentCount { get; }

        public TagCollection Tags { get; }

        private FavoriteCategory favoriteCategory;
        public FavoriteCategory FavoriteCategory
        {
            get => this.favoriteCategory;
            protected internal set => Set(ref this.favoriteCategory, value);
        }

        private string favoriteNote;
        public string FavoriteNote
        {
            get => this.favoriteNote;
            protected internal set => Set(ref this.favoriteNote, value);
        }

        private RevisionCollection revisions;
        public RevisionCollection Revisions
        {
            get => this.revisions;
            private set => Set(ref this.revisions, value);
        }


        private CommentCollection comments;
        public CommentCollection Comments => LazyInitializer.EnsureInitialized(ref this.comments, () => new CommentCollection(this));
        #endregion

        internal void RefreshMetaData(HtmlDocument doc)
        {
            ApiToken.Update(doc.DocumentNode.OuterHtml);
            var favNode = doc.GetElementbyId("fav");
            if (favNode != null)
            {
                var favContentNode = favNode.Element("div");
                this.FavoriteCategory = Client.Current.Favorites.GetCategory(favContentNode);
            }
            this.Rating.AnalyzeDocument(doc);
            if (this.Revisions is null)
            {
                this.Revisions = new RevisionCollection(this, doc);
            }

            this.Tags.Update(doc);
        }

        public IAsyncAction RefreshMetaDataAsync() => this.Comments.FetchAsync(false);

        protected override IAsyncOperation<LoadItemsResult<GalleryImage>> LoadItemAsync(int index)
        {
            return Run(token => Task.Run(async () =>
            {
                var html = await getDoc(index, token);
                token.ThrowIfCancellationRequested();
                var picRoot = html.GetElementbyId("gdt");
                var start = int.MaxValue;
                var end = 0;
                using (var db = new GalleryDb())
                {
                    db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                    foreach (var page in picRoot.Elements("div", "gdtl"))
                    {
                        var nodeA = page.Element("a");
                        var thumb = ThumbClient.FormatThumbUri(nodeA.Element("img").GetAttribute("src", ""));
                        if (thumb is null)
                            continue;

                        var tokens = nodeA.GetAttribute("href", "").Split(new[] { '/', '-' });
                        if (tokens.Length < 4 || tokens[tokens.Length - 4] != "s")
                        {
                            continue;
                        }

                        var pId = int.Parse(tokens[tokens.Length - 1], NumberStyles.Integer);
                        var imageKey = tokens[tokens.Length - 3].ToToken();
                        var gId = this.ID;
                        var imageModel = db.GalleryImageSet
                            .Include(gi => gi.Image)
                            .FirstOrDefault(gi => gi.GalleryId == gId && gi.PageId == pId);
                        if (imageModel != null)
                            // Load cache
                            await this[pId - 1].PopulateCachedImageAsync(imageModel, imageModel.Image);
                        else
                            this[pId - 1].Init(imageKey, thumb);

                        if (pId - 1 < start)
                            start = pId - 1;
                        if (pId > end)
                            end = pId;
                    }
                }
                return LoadItemsResult.Create(start, this.Skip(start).Take(end - start), false);

                async Task<HtmlDocument> getDoc(int imageIndex, CancellationToken cancellationToken, bool reIn = false)
                {
                    var pageIndex = imageIndex / this.pageSize;
                    var needLoadComments = !this.Comments.IsLoaded;
                    var uri = new Uri(this.GalleryUri, $"?{(needLoadComments ? "hc=1&" : "")}p={pageIndex.ToString()}");
                    var docOp = Client.Current.HttpClient.GetDocumentAsync(uri);
                    cancellationToken.Register(docOp.Cancel);
                    var doc = await docOp;
                    RefreshMetaData(doc);
                    if (needLoadComments)
                    {
                        this.Comments.AnalyzeDocument(doc);
                    }
                    if (reIn)
                    {
                        return doc;
                    }

                    var rows = doc.GetElementbyId("gdo2").Elements("div", "ths").Last().GetInnerText();
                    rows = rows.Substring(0, rows.IndexOf(' '));
                    var rowCount = int.Parse(rows);
                    this.PageSize = rowCount * 5;
                    if (doc.GetElementbyId("gdo4").Elements("div", "ths").Last().InnerText != "Large")
                    {
                        // 切换到大图模式
                        await Client.Current.HttpClient.GetAsync(new Uri("/?inline_set=ts_l", UriKind.Relative));
                        doc = await getDoc(imageIndex, cancellationToken, true);
                    }
                    else if (pageIndex != imageIndex / this.pageSize)
                    {
                        doc = await getDoc(imageIndex, cancellationToken, true);
                    }
                    return doc;
                }
            }, token));
        }

        public IAsyncOperation<string> FetchFavoriteNoteAsync()
        {
            return Run(async token =>
            {
                var doc = await Client.Current.HttpClient.GetDocumentAsync(new Uri($"gallerypopups.php?gid={this.ID}&t={this.Token.ToTokenString()}&act=addfav", UriKind.Relative));
                var favdel = doc.GetElementbyId("favdel");
                if (favdel != null)
                {
                    var favSet = false;
                    for (var i = 0; i < 10; i++)
                    {
                        var favNode = doc.GetElementbyId($"fav{i}");
                        var favNameNode = favNode.ParentNode.ParentNode.Elements("div").Skip(2).First();
                        var settings = Client.Current.Settings;
                        settings.FavoriteCategoryNames[i] = favNameNode.GetInnerText();
                        settings.StoreCache();
                        if (!favSet && favNode.GetAttribute("checked", false))
                        {
                            this.FavoriteCategory = Client.Current.Favorites[i];
                            favSet = true;
                        }
                    }
                    this.FavoriteNote = doc.DocumentNode.Descendants("textarea").First().GetInnerText();
                }
                else
                {
                    this.FavoriteCategory = Client.Current.Favorites.Removed;
                    this.FavoriteNote = "";
                }
                return this.FavoriteNote;
            });
        }

        public virtual IAsyncAction DeleteAsync()
        {
            return Task.Run(async () =>
            {
                var gid = this.ID;
                using (var db = new GalleryDb())
                {
                    var toDelete = db.GalleryImageSet
                        .Include(gi => gi.Image)
                        .Where(gi => gi.GalleryId == gid)
                        .ToList();
                    foreach (var item in toDelete)
                    {
                        var usingCount = db.GalleryImageSet
                            .Count(GalleryImageModel.FKEquals(item.ImageId));
                        if (usingCount <= 1)
                        {
                            var i = item.PageId - 1;
                            var file = default(StorageFile);
                            if (i < this.Count)
                            {
                                file = this[i].ImageFile;
                            }

                            if (file is null)
                            {
                                var folder = await GalleryImage.GetImageFolderAsync();
                                file = await folder.TryGetFileAsync(item.Image.FileName);
                            }
                            if (file != null)
                            {
                                await file.DeleteAsync();
                            }

                            db.ImageSet.Remove(item.Image);
                        }
                        db.GalleryImageSet.Remove(item);
                    }
                    await db.SaveChangesAsync();
                }
                for (var i = 0; i < this.Count; i++)
                {
                    UnloadAt(i);
                }
            }).AsAsyncAction();
        }
    }
}
