﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Trinity.ServiceFabric.Storage.External
{
    public partial class AzureBlobPartitionedImageStorage : IPartitionedImageStorage
    {
        private string connectionString;
        private string storageContainer;
        private string storageFolder;

        public Func<Stream, ICellStreamReader> CreateCellStreamReader { get; set; } = (stream) => new CellStreamReader(stream);
        public Func<Stream, ICellStreamWriter> CreateCellStreamWriter { get; set; } = (stream) => new CellStreamWriter(stream);

        public AzureBlobPartitionedImageStorage(string connectionString, string storageContainer, string folder)
        {
            this.connectionString = connectionString;
            this.storageContainer = storageContainer;
            this.storageFolder = folder;
        }

        public ImagePartitionSignature LoadPartitionSignature(int partition)
        {
            var bytes = DownloadBlockBlob(Path.Combine(storageFolder, $"{partition}.sig"));
            if (bytes == null)
                return new ImagePartitionSignature { PartitionId = partition };

            var json = Encoding.UTF8.GetString(bytes);
            return JsonConvert.DeserializeObject<ImagePartitionSignature>(json);
        }

        public void SavePartitionSignature(ImagePartitionSignature signature)
        {
            var json = JsonConvert.SerializeObject(signature);
            UploadBlockBlob(Path.Combine(storageFolder, $"{signature.PartitionId}.sig"), Encoding.UTF8.GetBytes(json));
        }

        public string LoadImagePartition(int partition)
        {
            var blob = Container.GetBlockBlobReference(Path.Combine(storageFolder, $"{partition}.image"));

            using (var reader = CreateCellStreamReader(blob.OpenRead()))
            {
                long cellId;
                ushort cellType;
                byte[] content;

                while (reader.ReadCell(out cellId, out cellType, out content))
                {
                    Global.LocalStorage.SaveCell(cellId, content, cellType);
                }
            }

            return blob.Properties.ContentMD5;
        }

        public string SaveImagePartition(int partition)
        {
            Container.CreateIfNotExists();
            var blob = Container.GetBlockBlobReference(Path.Combine(storageFolder, $"{partition}.image"));

            var cellIds = Global.LocalStorage.GenericCellAccessor_Selector()
                .Where(c => Global.CloudStorage.GetPartitionIdByCellId(c.CellID) == partition)
                .Select(c => c.CellID).ToList();

            using (var writer = CreateCellStreamWriter(blob.OpenWrite()))
            {
                if (Global.MyServerId == 0)
                    Debug.WriteLine($"Partition#{partition} begin saving");

                foreach (var id in cellIds)
                {
                    byte[] bytes;
                    ushort cellType;

                    Global.LocalStorage.LoadCell(id, out bytes, out cellType);
                    writer.WriteCell(id, cellType, bytes);
                }

                if (Global.MyServerId == 0)
                    Debug.WriteLine($"Partition#{partition} end saving");
            }

            return blob.Properties.ContentMD5;
        }
    }

    public partial class AzureBlobPartitionedImageStorage
    {
        private CloudBlobClient blobClient;
        private CloudBlobClient BlobClient => UseBlobClient();
        private CloudBlobContainer Container => BlobClient.GetContainerReference(storageContainer);

        private CloudBlobClient UseBlobClient()
        {
            if (blobClient == null)
            {
                var storageAccount = CloudStorageAccount.Parse(connectionString);
                blobClient = storageAccount.CreateCloudBlobClient();
            }
            return blobClient;
        }

        private byte[] DownloadBlockBlob(string blobName)
        {
            //if (!Container.Exists())
            //    return null;

            var blob = Container.GetBlockBlobReference(blobName);
            if (!blob.Exists())
                return null;

            using (var ms = new MemoryStream())
            {
                blob.DownloadToStream(ms);
                return ms.ToArray();
            }
        }

        private void UploadBlockBlob(string blobName, byte[] bytes)
        {
            Container.CreateIfNotExists();

            var blob = Container.GetBlockBlobReference(blobName);
            blob.UploadFromByteArray(bytes, 0, bytes.Length);
        }
    }
}