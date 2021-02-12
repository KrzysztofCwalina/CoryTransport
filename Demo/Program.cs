using Azure.Core.Http.NoAlloc;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.IO;
using System.Threading.Tasks;

class Program
{
    static async Task Main() {
        string connectionString = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");

        var options = new BlobClientOptions();
        options.Transport = new NoAllocTransport();

        var client = new BlobClient(connectionString, "test-container", "testblob.txt", options);

        BlobDownloadInfo result = await client.DownloadAsync();
        var reader = new StreamReader(result.Content);
        var content = reader.ReadToEnd();
        Console.WriteLine(content);
    }
}

