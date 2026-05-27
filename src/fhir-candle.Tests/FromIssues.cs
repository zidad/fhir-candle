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
    /// Tests that the FHIR R4 standard search parameter `CarePlan.patient`
    /// (defined as `CarePlan.subject.where(resolve() is Patient)`) returns
    /// CarePlans whose subject is a Patient reference. fhir-candle currently
    /// matches on `subject=` but returns zero results for `patient=` on the
    /// same data.
    /// </summary>
    [Fact]
    public void CarePlanPatientSearchParameterShouldResolveSubject()
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

        string carePlanJson = """
            {
              "resourceType": "CarePlan",
              "id": "cp-1",
              "status": "active",
              "intent": "plan",
              "subject": { "reference": "Patient/test-patient-123" }
            }
            """;

        FhirRequestContext createCtx = new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "PUT",
            Url = store.Config.BaseUrl + "/CarePlan",
            Forwarded = null,
            Authorization = null,
            SourceContent = carePlanJson,
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
            ResourceType = "CarePlan",
        };
        store.InstanceUpdate(createCtx, out _).ShouldBeTrue();

        // Sanity check — the `subject=` form works.
        FhirRequestContext subjectSearch = new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "GET",
            Url = store.Config.BaseUrl + "/CarePlan",
            Forwarded = null,
            Authorization = null,
            UrlQuery = "subject=Patient/test-patient-123",
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
            ResourceType = "CarePlan",
        };
        store.TypeSearch(subjectSearch, out FhirResponseContext subjectResp).ShouldBeTrue();
        JsonSerializer.Deserialize<MinimalBundle>(subjectResp.SerializedResource)!.Total.ShouldBe(1);

        // The R4-defined `patient=` search parameter should resolve to the same
        // CarePlan (https://www.hl7.org/fhir/R4/careplan.html#search). Currently
        // candle returns total = 0.
        FhirRequestContext patientSearch = new()
        {
            TenantName = store.Config.ControllerName,
            Store = store,
            HttpMethod = "GET",
            Url = store.Config.BaseUrl + "/CarePlan",
            Forwarded = null,
            Authorization = null,
            UrlQuery = "patient=Patient/test-patient-123",
            SourceFormat = "application/fhir+json",
            DestinationFormat = "application/fhir+json",
            ResourceType = "CarePlan",
        };
        store.TypeSearch(patientSearch, out FhirResponseContext patientResp).ShouldBeTrue();
        JsonSerializer.Deserialize<MinimalBundle>(patientResp.SerializedResource)!.Total.ShouldBe(
            1,
            "FHIR R4 defines CarePlan?patient as CarePlan.subject.where(resolve() is Patient); the seeded CarePlan has a Patient subject");
    }
}
