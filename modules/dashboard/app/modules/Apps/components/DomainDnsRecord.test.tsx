import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { DomainDnsRecord } from "./DomainDnsRecord";

afterEach(cleanup);
it.each([
  ["example.com", "example.com", "@"],
  ["staging.api.example.co.uk", "example.co.uk", "staging.api"],
  ["_minicloud-verification.example.com", "example.com", "_minicloud-verification"]
])("formats the record name for %s in its explicit DNS zone", (name, zone, expected) => {
  render(<DomainDnsRecord type="TXT" name={name} value="value" />);
  expect(screen.getByText(name)).toBeInTheDocument();
  fireEvent.click(screen.getByText("Does your provider add the domain name automatically?"));
  fireEvent.change(screen.getByLabelText("DNS zone"), { target: { value: zone } });
  expect(screen.getByText(expected)).toBeInTheDocument();
});
it("rejects unrelated zone suffixes without changing the full record name", () => {
  render(<DomainDnsRecord type="A" name="notexample.com" value="192.0.2.1" />);
  fireEvent.change(screen.getByLabelText("DNS zone"), { target: { value: "example.com" } });
  expect(screen.getByRole("alert")).toHaveTextContent("does not belong");
  expect(screen.getByText("notexample.com")).toBeInTheDocument();
});
