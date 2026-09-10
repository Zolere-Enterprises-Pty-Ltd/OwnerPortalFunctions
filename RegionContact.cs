using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace OwnerPortalFunctions;

/// <summary>
/// The phone number an owner in a given region should call.
///
/// <para>
/// ONE SOURCE, AND IT IS A TABLE, NOT A SWITCH. The four regional numbers live in
/// <c>dbo.RegionOffice</c>, one row per region, seeded by
/// <c>OwnerPortal.Server/Migrations/seed_RegionOffice_phone_numbers.sql</c>. The portal web app already
/// reads that table from <c>RegionalContactService</c> for the owner answer finder's not-found path, so
/// a number changed there changes everywhere at once with no deploy. A <c>switch</c> on region name in
/// this file would have been the fourth place the same four numbers were written down, and the one
/// nobody would remember to update.
/// </para>
///
/// <para>
/// NEVER RETURNS BLANK. <see cref="ForListingAsync"/> falls back to the general number, so the sentence
/// "please call us on …" in an owner-facing email cannot render with a hole in it. A wrong-but-answerable
/// number reaches a human who can redirect; an empty one reaches nobody. The fallback fires whenever the
/// listing has no town, the town is absent from <c>dbo.Regions</c>, the region has no active
/// <c>RegionOffice</c> row, or that row's <c>PhoneNumber</c> is NULL — and every one of those is logged
/// at Warning, because a region silently serving the general number is a data problem, not a design.
/// </para>
/// </summary>
public static class RegionContact
{
    /// <summary>
    /// The number used when a listing does not resolve to a regional office. Defaults to the
    /// number this email carried, hardcoded, before regional numbers existed — so an unset app setting
    /// degrades to today's behaviour rather than to a blank line. It is the same value the portal web app
    /// holds as <c>OwnerAnswer:CompanyContactPhone</c>.
    /// </summary>
    private const string DefaultGeneralPhoneNumber = "(02) 5325 8561";

    /// <summary>
    /// Listing to office phone, in one query.
    ///
    /// <para>
    /// The town key is <c>COALESCE(Town, CityAddress)</c> and not <c>Listings.Town</c>, matching
    /// <c>ListingRegionSql.Town</c> in the portal repo. That column is NULL on more than half of
    /// <c>Listings</c> — the Guesty loader leaves it empty on new rows and clears it on archive — and the
    /// correct town is in <c>CityAddress</c>. Joining on the raw column would drop those listings to the
    /// general number without anything looking wrong.
    /// </para>
    /// </summary>
    private const string PhoneSql = """
        SELECT TOP (1) o.PhoneNumber
        FROM dbo.Listings AS l
        INNER JOIN dbo.Regions AS r
            ON r.City = COALESCE(NULLIF(LTRIM(RTRIM(l.Town)), ''), NULLIF(LTRIM(RTRIM(l.CityAddress)), ''))
        INNER JOIN dbo.RegionOffice AS o
            ON o.Region = r.Region
        WHERE l.GuestyId = @GuestyId
          AND o.IsActive = 1
          AND o.PhoneNumber IS NOT NULL
          AND LTRIM(RTRIM(o.PhoneNumber)) <> ''
        """;

    /// <summary>General number, from <c>GeneralPhoneNumber</c>, never blank.</summary>
    public static string GeneralPhoneNumber
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("GeneralPhoneNumber");
            return string.IsNullOrWhiteSpace(configured) ? DefaultGeneralPhoneNumber : configured.Trim();
        }
    }

    /// <summary>
    /// The number to print for this listing's owner. Never null, never empty, never throws — a lookup
    /// failure here must not stop an approval email that is otherwise correct, so a SQL error is logged
    /// and the general number is returned.
    /// </summary>
    public static async Task<string> ForListingAsync(
        SqlConnection connection,
        string guestyId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(PhoneSql, connection);
            command.Parameters.AddWithValue("@GuestyId", guestyId);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            if (result is string phone && !string.IsNullOrWhiteSpace(phone))
                return phone.Trim();

            logger.LogWarning(
                "No regional office phone resolved for listing {GuestyId}; using the general number. "
              + "Check the listing's town against dbo.Regions and that dbo.RegionOffice has an active row "
              + "with a PhoneNumber for its region.",
                guestyId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Region phone lookup failed for listing {GuestyId}; using the general number.", guestyId);
        }

        return GeneralPhoneNumber;
    }
}
