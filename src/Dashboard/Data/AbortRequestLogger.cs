﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Azure.WebJobs.Storage;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Dashboard.Data
{
    public class AbortRequestLogger : IAbortRequestLogger
    {
        private readonly CloudBlobDirectory _directory;

        [SuppressMessage("Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0"), CLSCompliant(false)]
        public AbortRequestLogger(CloudBlobClient client)
            : this(client.GetContainerReference(DashboardContainerNames.Dashboard)
                .GetDirectoryReference(DashboardDirectoryNames.AbortRequestLogs))
        {
        }

        private AbortRequestLogger(CloudBlobDirectory directory)
        {
            _directory = directory;
        }

        public void LogAbortRequest(string queueName)
        {
            CloudBlockBlob blob = _directory.GetBlockBlobReference(queueName);

            try
            {
                blob.UploadText(String.Empty);
            }
            catch (StorageException exception)
            {
                if (exception.IsNotFound())
                {
                    blob.Container.CreateIfNotExists();
                    blob.UploadText(String.Empty);
                }
                else
                {
                    throw;
                }
            }
        }

        public bool HasRequestedAbort(string queueName)
        {
            CloudBlockBlob blob = _directory.GetBlockBlobReference(queueName);

            try
            {
                blob.FetchAttributes();
                return true;
            }
            catch (StorageException exception)
            {
                if (exception.IsNotFound())
                {
                    return false;
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
