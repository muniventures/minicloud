import { describe, expect, it } from "vitest";
import { appsFixtureApplications } from "../../api/applicationsFixtures";
import {
  appMatchesFilters,
  matchedEnvironment,
  matchesSearch,
  orderedEnvironments,
  visibleEnvironments,
} from "./appCollection";

const apps = appsFixtureApplications();
const munipages = apps.find((a) => a.id === "app_munipages")!;

describe("orderedEnvironments", () => {
  it("lists persistent environments before previews, by name", () => {
    const names = orderedEnvironments(munipages).map((e) => e.name);
    expect(names).toEqual(["production", "staging", "feat-checkout", "fix-billing"]);
  });
});

describe("matchesSearch", () => {
  it("finds an app by slug, environment name, and domain", () => {
    expect(matchesSearch(munipages, "munipages")).toBe(true);
    expect(matchesSearch(munipages, "staging")).toBe(true);
    expect(matchesSearch(munipages, "staging.app.example.com")).toBe(true);
    expect(matchesSearch(munipages, "nope")).toBe(false);
  });
});

describe("environment filter matching", () => {
  it("never combines predicates across environments", () => {
    // production has succeeded deployment; staging is persistent.
    // kind=persistent + deployment=failed must match staging (both true on staging).
    expect(matchedEnvironment(munipages, { kind: "persistent", deployment: "failed" })?.name).toBe("staging");
    // kind=preview + deployment=failed matches nothing: prod succeeded, staging persistent.
    expect(matchedEnvironment(munipages, { kind: "preview", deployment: "failed" })).toBeNull();
    // deployment=none matches the preview with no deployments.
    expect(matchedEnvironment(munipages, { deployment: "none" })?.name).toBe("fix-billing");
  });

  it("appMatchesFilters is true only when one environment satisfies all predicates", () => {
    expect(appMatchesFilters(munipages, { kind: "preview", deployment: "failed" })).toBe(false);
    expect(appMatchesFilters(munipages, { kind: "persistent", deployment: "failed" })).toBe(true);
    expect(appMatchesFilters(munipages, {})).toBe(true);
  });
});

describe("visibleEnvironments", () => {
  it("collapses previews unless expanded, attention, or filtered match", () => {
    const collapsed = visibleEnvironments(munipages, {});
    expect(collapsed.visible.map((e) => e.name)).toEqual(["production", "staging"]);
    expect(collapsed.hiddenPreviewCount).toBe(2);

    const expanded = visibleEnvironments(munipages, { previewsExpanded: true });
    expect(expanded.visible).toHaveLength(4);
    expect(expanded.hiddenPreviewCount).toBe(0);

    // Filtered preview stays visible without expanding.
    const matched = visibleEnvironments(munipages, { matchedId: "runtime_munipages_feat_checkout" });
    expect(matched.visible.map((e) => e.name)).toContain("feat-checkout");
  });
});
