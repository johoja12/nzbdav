using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Requests;
using Serilog;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreReadonlyCollection : BaseStoreCollection
{
    protected override Task<StoreItemResult> CopyAsync(CopyRequest request)
    {
        Log.Warning($"Cannot copy item `{request.Name}`: Forbidden.");
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreItemResult> CreateItemAsync(CreateItemRequest request)
    {
        Log.Warning($"Cannot create item `{request.Name}`: Forbidden.");
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreCollectionResult> CreateCollectionAsync(CreateCollectionRequest request)
    {
        Log.Warning($"Cannot create directory `{request.Name}`: Forbidden");
        return Task.FromResult(new StoreCollectionResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreItemResult> MoveItemAsync(MoveItemRequest request)
    {
        Log.Warning($"Cannot move item `{request.SourceName}`: Forbidden");
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        Log.Warning("[BaseReadonlyCollection] Cannot delete '{Name}' from collection type {CollectionType}: Forbidden",
            request.Name, GetType().Name);
        return Task.FromResult(DavStatusCode.Forbidden);
    }
}