﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Util;

namespace nntpAutoposter
{
    class IndexerVerifier
    {
        private Object monitor = new Object();
        private AutoPosterConfig configuration;
        private Task MyTask;
        private Boolean StopRequested;

        public IndexerVerifier(AutoPosterConfig configuration)
        {
            this.configuration = configuration;
            StopRequested = false;
            MyTask = new Task(IndexerVerifierTask, TaskCreationOptions.LongRunning);
        }

        public void Start()
        {
            MyTask.Start();
        }

        public void Stop()
        {
            lock (monitor)
            {
                StopRequested = true;
                Monitor.Pulse(monitor);
            }            
            MyTask.Wait();
        }

        private void IndexerVerifierTask()
        {
            while (!StopRequested)
            {
                VerifyUploadsOnIndexer();
                lock (monitor)
                {
                    if (StopRequested)
                    {
                        break;
                    }
                    Monitor.Wait(monitor, configuration.VerifierIntervalMinutes * 60 * 1000);
                }
            }
        }

        private void VerifyUploadsOnIndexer()
        {
            foreach (var upload in DBHandler.Instance.GetUploadEntriesToVerify())
            {
                try
                {
                    if (UploadIsOnIndexer(upload))
                    {
                        upload.SeenOnIndexAt = DateTime.UtcNow;
                        DBHandler.Instance.UpdateUploadEntry(upload);
                        Console.WriteLine("Release [{0}] has been found on the indexer.", upload.CleanedName);

                        if (upload.RemoveAfterVerify)
                        {
                            String fullPath = Path.Combine(configuration.BackupFolder.FullName, upload.Name);
                            FileAttributes attributes = File.GetAttributes(fullPath);
                            if (attributes.HasFlag(FileAttributes.Directory))
                                Directory.Delete(fullPath, true);
                            else
                                File.Delete(fullPath);
                        }
                    }
                    else
                    {
                        RepostIfRequired(upload);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Could not verify release [{0}] on index:", upload.CleanedName);
                    Console.WriteLine(ex.ToString());
                    //TODO: Log.
                }
            }
        }

        private Boolean UploadIsOnIndexer(UploadEntry upload)
        {
            var postAge = (Int32)Math.Ceiling((DateTime.UtcNow - upload.UploadedAt.Value).TotalDays);
            String verificationGetUrl = String.Format(
                configuration.SearchUrl,
                Uri.EscapeDataString(upload.CleanedName),
                postAge);

            using (HttpClient client = new HttpClient())
            {
                Task<HttpResponseMessage> getTask = null;
                try
                {
                    getTask = client.GetAsync(verificationGetUrl);
                    getTask.Wait(60 * 1000);
                    if (getTask.IsCompleted)
                    {
                        if (getTask.IsFaulted)
                            throw getTask.Exception;
                        if (getTask.Result == null)
                            throw new Exception("No valid HttpResponse received.");

                        if (!getTask.Result.IsSuccessStatusCode)
                            throw new Exception("Error when verifying on indexer: "
                                + getTask.Result.StatusCode + " " + getTask.Result.ReasonPhrase);

                        return FindUploadInResponse(upload, getTask.Result);   //This blocks until the result is available.
                    }
                    throw new Exception("No valid HttpResponse could be received within the timeout period.");
                }
                finally
                {
                    if (getTask != null && getTask.IsCompleted && getTask.Result != null)
                        getTask.Result.Dispose();
                }
            }
        }

        private Boolean FindUploadInResponse(UploadEntry upload, HttpResponseMessage httpResponseMessage)
        {
            Task<Stream> responseStreamTask = null;
            try
            {
                responseStreamTask = httpResponseMessage.Content.ReadAsStreamAsync();
                using(XmlReader xmlReader = XmlReader.Create(responseStreamTask.Result))
                {
                    SyndicationFeed feed = SyndicationFeed.Load(xmlReader);
                    foreach(var item in feed.Items)
                    {
                        Decimal similarityPercentage =
                            LevenshteinDistance.SimilarityPercentage(item.Title.Text, upload.CleanedName);
                        if (similarityPercentage > configuration.VerifySimilarityPercentageTreshold)
                            return true;
                    }
                }
            }
            finally
            {
                if (responseStreamTask != null && responseStreamTask.IsCompleted && responseStreamTask.Result != null)
                    responseStreamTask.Result.Dispose();
            }
            return false;
        }

        private void RepostIfRequired(UploadEntry upload)
        {
            var AgeInMinutes = (DateTime.UtcNow - upload.UploadedAt.Value).TotalMinutes;
            var repostTreshold = Math.Pow(upload.Size, (1 / 2.45)) / 60; 
            //This is a bit of guesswork, a 15 MB item will repost after about 15 minutes, 
            // a  5 GB item will repost after about 2h30.
            // a 15 GB item will repost after about 4h00.
            // a 50 GB item will repost after about 6h30.
            
            //In any case, it gets overruled by the configuration here.
            if (repostTreshold < configuration.MinRepostAgeMinutes)
                repostTreshold = configuration.MinRepostAgeMinutes;
            if (repostTreshold > configuration.MaxRepostAgeMinutes)
                repostTreshold = configuration.MaxRepostAgeMinutes;

            if(AgeInMinutes > repostTreshold)
            {
                UploadEntry repost = new UploadEntry();
                repost.Name = upload.Name;
                repost.RemoveAfterVerify = upload.RemoveAfterVerify;
                repost.Cancelled = false;
                repost.Size = upload.Size;
                DBHandler.Instance.AddNewUploadEntry(repost);   
                //This implicitly cancels all other uploads with the same name so no need to update the upload itself.
            }
        }
    }
}