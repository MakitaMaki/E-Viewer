﻿using ExClient.Models;
using Opportunity.MvvmUniverse;
using Opportunity.MvvmUniverse.AsyncHelpers;
using Opportunity.MvvmUniverse.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Xaml.Data;
using static System.Runtime.InteropServices.WindowsRuntime.AsyncInfo;
using Windows.Graphics.Imaging;

namespace ExClient.Galleries
{
    public class CachedGallery : Gallery
    {
        private sealed class CachedGalleryList : GalleryList<CachedGallery, GalleryModel>
        {
            public static IAsyncOperation<ObservableCollection<Gallery>> LoadList()
            {
                return Task.Run<ObservableCollection<Gallery>>(() =>
                {
                    using (var db = new GalleryDb())
                    {
                        db.ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
                        var query = from gm in db.GallerySet
                                    where gm.Images.Count != 0
                                    where db.SavedSet.FirstOrDefault(sm => sm.GalleryId == gm.Id) == null
                                    orderby gm.Id descending
                                    select gm;
                        return new CachedGalleryList(query.ToList());
                    }
                }).AsAsyncOperation();
            }

            private CachedGalleryList(List<GalleryModel> galleries)
                : base(galleries)
            {
            }

            protected override CachedGallery Load(GalleryModel model)
            {
                var c = new CachedGallery(model);
                var ignore = c.InitAsync();
                return c;
            }
        }

        public static IAsyncOperation<ObservableCollection<Gallery>> LoadCachedGalleriesAsync()
        {
            return CachedGalleryList.LoadList();
        }

        public static IAsyncActionWithProgress<double> ClearCachedGalleriesAsync()
        {
            return Run<double>(async (token, progress) =>
            {
                progress.Report(double.NaN);
                using (var db = new GalleryDb())
                {
                    var query = from gm in db.GallerySet
                                where gm.Images.Count != 0
                                where db.SavedSet.FirstOrDefault(sm => sm.GalleryId == gm.Id) == null
                                select gm.Images;
                    var cacheDic = query.ToDictionary(dm => dm.First().OwnerId.ToString());
                    var saveDic = db.SavedSet.Select(sm => sm.GalleryId).ToDictionary(id => id.ToString());
                    double count = cacheDic.Count;
                    var i = 0;
                    foreach (var item in cacheDic)
                    {
                        progress.Report(i / count);
                        var folder = await ApplicationData.Current.LocalCacheFolder.CreateFolderAsync(item.Key, CreationCollisionOption.OpenIfExists);
                        await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        db.ImageSet.RemoveRange(item.Value);
                        i++;
                    }
                    //Delete empty folders
                    var folders = await ApplicationData.Current.LocalCacheFolder.GetItemsAsync();
                    foreach (var item in folders)
                    {
                        if (!saveDic.ContainsKey(item.Name) && long.TryParse(item.Name, out var r))
                            await item.DeleteAsync();
                    }
                    await db.SaveChangesAsync();
                }
            });
        }

        internal CachedGallery(GalleryModel model)
            : base(model)
        {
            this.loadingPageArray = new IAsyncAction[MathHelper.GetPageCount(model.RecordCount, PageSize)];
        }

        internal ImageModel[] ImageModels { get; private set; }

        internal void LoadImageModels()
        {
            if (this.ImageModels != null)
                return;
            this.ImageModels = new ImageModel[this.RecordCount];
            using (var db = new GalleryDb())
            {
                db.ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
                var gid = this.Id;
                var models = from im in db.ImageSet
                             where im.OwnerId == gid
                             select im;
                foreach (var item in models)
                {
                    this.ImageModels[item.PageId - 1] = item;
                }
            }
        }

        protected override IAsyncOperation<IList<GalleryImage>> LoadPageAsync(int pageIndex)
        {
            return Task.Run(async () =>
            {
                try
                {
                    return await base.LoadPageAsync(pageIndex);
                }
                catch
                {
                    this.LoadImageModels();
                    var currentPageSize = MathHelper.GetSizeOfPage(this.RecordCount, PageSize, pageIndex);
                    var loadList = new GalleryImage[currentPageSize];
                    for (var i = 0; i < currentPageSize; i++)
                    {
                        var model = this.ImageModels[this.Count + i];
                        if (model == null)
                        {
                            loadList[i] = new GalleryImagePlaceHolder(this, this.Count + i + 1);
                        }
                        else
                        {
                            loadList[i] = await GalleryImage.LoadCachedImageAsync(this, model)
                                    ?? new GalleryImage(this, model.PageId, model.ImageKey, null);
                        }
                    }
                    return loadList;
                }
            }).AsAsyncOperation();
        }

        private readonly IAsyncAction[] loadingPageArray;

        internal IAsyncAction LoadImageAsync(GalleryImagePlaceHolder image)
        {
            var pageIndex = MathHelper.GetPageIndexOfRecord(PageSize, image.PageId - 1);
            var lpAc = this.loadingPageArray[pageIndex];
            if (lpAc != null && lpAc.Status == AsyncStatus.Started)
                return lpAc;
            return this.loadingPageArray[pageIndex] = Run(async token =>
            {
                var images = await base.LoadPageAsync(pageIndex);
                var offset = MathHelper.GetStartIndexOfPage(PageSize, pageIndex);
                for (var i = 0; i < images.Count; i++)
                {
                    var ph = this[i + offset] as GalleryImagePlaceHolder;
                    if (ph == null)
                        continue;
                    this[i + offset] = images[i];
                }
                await Task.Yield();
                this.loadingPageArray[pageIndex] = null;
            }).AsMulticast();
        }

        public override IAsyncAction DeleteAsync()
        {
            this.ImageModels = null;
            Array.Clear(this.loadingPageArray, 0, this.loadingPageArray.Length);
            return base.DeleteAsync();
        }

        public override IAsyncActionWithProgress<SaveGalleryProgress> SaveGalleryAsync(ConnectionStrategy strategy)
        {
            return Run<SaveGalleryProgress>(async (token, p) =>
            {
                p.Report(new SaveGalleryProgress { ImageCount = this.RecordCount, ImageLoaded = -1 });
                for (var i = 0; i < this.Count; i++)
                {
                    if (this[i] is GalleryImagePlaceHolder ph)
                    {
                        token.ThrowIfCancellationRequested();
                        await ph.LoadImageAsync(false, strategy, true);
                    }
                }
                var load = base.SaveGalleryAsync(strategy);
                load.Progress = (sender, pro) => p.Report(pro);
                token.Register(load.Cancel);
                await load;
            });
        }

        protected override IAsyncAction InitOverrideAsync()
        {
            return AsyncWrapper.CreateCompleted();
        }

        protected override IAsyncOperation<SoftwareBitmap> GetThumbAsync()
        {
            return Run(async token =>
            {
                var r = await base.GetThumbAsync();
                if (r != null)
                    return r;
                var f = await GetFolderAsync();
                var file = (await f.GetFilesAsync(Windows.Storage.Search.CommonFileQuery.DefaultQuery, 0, 1)).SingleOrDefault();
                if (file == null)
                    return null;
                try
                {
                    using (var stream = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem))
                    {
                        var decoder = await BitmapDecoder.CreateAsync(stream);
                        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
    }
}
