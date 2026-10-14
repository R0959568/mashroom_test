using CitibikeIngest.Api.Dtos;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Mappers;

public static class S3FilesMapper
{
    public static RemoteFileDto ToDto(this RemoteFile file) =>
        new(file.Name, file.Region, file.Year, file.Month, file.Label, file.SizeBytes, file.LastModified);

    public static NewFilesDto ToDto(this NewFiles listing) => new(
        listing.Since.Since,
        listing.Since.Source.ToApi(),
        listing.Files.Count,
        listing.TotalBytes,
        listing.Files.Select(ToDto).ToList());

    /// <summary>Enums are lowercase strings on the wire, the same spelling as in the metadata tables.</summary>
    public static string ToApi<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();
}
