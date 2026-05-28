extern alias candleR4;
extern alias coreR4;

using FhirCandle.Models;
using FhirCandle.Storage;
using FhirCandle.Utils;
using fhir.candle.Tests.Models;
using System.Text.Json;
using Xunit.Abstractions;
using candleR4::FhirCandle.Storage;
using fhir.candle.Tests.Extensions;
using Shouldly;
using System.Net;
using Hl7.FhirPath;
using fhir.candle.Services;
using static FhirCandle.Storage.Common;

namespace fhir.candle.Tests;

public class FromIssueTestsR4
{
    /// <summary>
    /// Tests to ensure transaction response bundles use the correct formatting
    /// of status codes.
    /// For issue: https://github.com/FHIR/fhir-candle/issues/26
    /// Fixed by: https://github.com/FHIR/fhir-candle/commit/e8e0df0ced298ce62f005830e5ec4c751aca419f
    /// </summary>
    [Fact]
    public void TransactionResponseStatusMissingCode()
    {
        TenantConfiguration config = new()
        {
            FhirVersion = FhirReleases.FhirSequenceCodes.R4,
            ControllerName = "r4",
            BaseUrl = "http://localhost/fhir/r4",
            LoadDirectory = null,
            AllowExistingId = true,
            AllowCreateAsUpdate = true,
        };

        IFhirStore store = new VersionedFhirStore();
        store.Init(config);

        string json = """
            {
              "resourceType": "Bundle",
              "type": "transaction",
              "entry": [
                {
                  "request": {
                    "method": "POST",
                    "url": "Patient"
                  },
                  "resource": {
                    "resourceType": "Patient",
                    "id": "example"
                  }
                }
              ]
            }
            """;

        FhirRequestContext ctx;
        FhirResponseContext response;
        bool success;

        ctx = new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "POST",
            Url = store.Config.BaseUrl,
            Forwarded = null,
            Authorization = null,
            SourceContent = json,
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
        };

        success = store.ProcessBundle(ctx, out response);

        success.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.SerializedResource.ShouldNotBeNullOrEmpty();

        MinimalBundle? bundle = JsonSerializer.Deserialize<MinimalBundle>(response.SerializedResource);
        bundle.ShouldNotBeNull();
        bundle.Entries.ShouldNotBeNullOrEmpty();
        bundle.Entries.Count().ShouldBe(1);

        MinimalBundle.MinimalEntry entry = bundle.Entries.First();

        entry.Response.ShouldNotBeNull();
        entry.Response.Status.ShouldBe("201 Created");
    }

    /// <summary>
    /// Documents the (correct) behaviour of the FHIR R4 `CarePlan.patient`
    /// search parameter, defined as `CarePlan.subject.where(resolve() is Patient)`.
    ///
    /// candle evaluates the spec-defined FHIRPath expression literally:
    /// `resolve()` actually fetches the referenced resource. When the target
    /// Patient is in the store, `patient=` matches; when the reference is
    /// dangling, it doesn't. `subject=` matches on the reference URL alone
    /// and never calls `resolve()`.
    ///
    /// This is stricter than some FHIR servers (notably Microsoft's open-source
    /// SQL-backed FHIR Server / Azure Health Data Services) that index
    /// reference search parameters by the resource-type prefix in the URL and
    /// skip `resolve()`. Consumers used to that looser behaviour can see
    /// surprising zero-result searches when their reference targets aren't
    /// stored alongside the referrer.
    /// </summary>
    [Fact]
    public void CarePlanPatientSearchResolvesReferencedPatient()
    {
        TenantConfiguration config = new()
        {
            FhirVersion = FhirReleases.FhirSequenceCodes.R4,
            ControllerName = "r4",
            BaseUrl = "http://localhost/fhir/r4",
            LoadDirectory = null,
            AllowExistingId = true,
            AllowCreateAsUpdate = true,
        };

        IFhirStore store = new VersionedFhirStore();
        store.Init(config);

        FhirRequestContext PutResource(string resourceType, string body) => new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "PUT",
            Url = store.Config.BaseUrl + "/" + resourceType,
            Forwarded = null,
            Authorization = null,
            SourceContent = body,
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
            ResourceType = resourceType,
        };

        FhirRequestContext Search(string resourceType, string urlQuery) => new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "GET",
            Url = store.Config.BaseUrl + "/" + resourceType,
            Forwarded = null,
            Authorization = null,
            UrlQuery = urlQuery,
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
            ResourceType = resourceType,
        };

        // Seed a CarePlan with a dangling Patient reference.
        store.InstanceUpdate(
            PutResource("CarePlan",
                """{"resourceType":"CarePlan","id":"cp-1","status":"active","intent":"plan","subject":{"reference":"Patient/test-patient-123"}}"""),
            out _).ShouldBeTrue();

        // subject= matches on the reference value directly — works regardless
        // of whether the target Patient exists.
        store.TypeSearch(Search("CarePlan", "subject=Patient/test-patient-123"), out FhirResponseContext subjectResp).ShouldBeTrue();
        JsonSerializer.Deserialize<MinimalBundle>(subjectResp.SerializedResource)!.Total.ShouldBe(1);

        // patient= invokes resolve(); with no Patient in the store, resolve()
        // returns nothing and the FHIRPath predicate excludes this CarePlan.
        store.TypeSearch(Search("CarePlan", "patient=Patient/test-patient-123"), out FhirResponseContext beforeResp).ShouldBeTrue();
        JsonSerializer.Deserialize<MinimalBundle>(beforeResp.SerializedResource)!.Total.ShouldBe(
            0,
            "with a dangling reference, resolve() returns nothing and the patient= predicate misses");

        // Seed the referenced Patient; resolve() now succeeds.
        store.InstanceUpdate(
            PutResource("Patient",
                """{"resourceType":"Patient","id":"test-patient-123"}"""),
            out _).ShouldBeTrue();

        store.TypeSearch(Search("CarePlan", "patient=Patient/test-patient-123"), out FhirResponseContext afterResp).ShouldBeTrue();
        JsonSerializer.Deserialize<MinimalBundle>(afterResp.SerializedResource)!.Total.ShouldBe(
            1,
            "with the target Patient present, resolve() returns it, `is Patient` holds, and the CarePlan matches");
    }
}
