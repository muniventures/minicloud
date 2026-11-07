import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { ServiceUrls } from "./ServiceUrls";

afterEach(cleanup);

it("shows every domain URL by service and copies the selected URL", () => {
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
  render(<ServiceUrls services={[
    { name: "admin", urls: ["https://admin.example"] },
    { name: "web", urls: ["https://web.example", "https://alias.example"] },
    { name: "worker", urls: [] },
  ]} />);
  expect(screen.getAllByRole("link").map((link) => link.getAttribute("href"))).toEqual([
    "https://admin.example", "https://web.example", "https://alias.example",
  ]);
  expect(screen.queryByText("worker")).not.toBeInTheDocument();
  fireEvent.click(screen.getAllByRole("button", { name: "Copy" })[2]);
  expect(writeText).toHaveBeenCalledWith("https://alias.example");
});

it("shows an empty state when no services have domains", () => {
  render(<ServiceUrls services={[{ name: "worker", urls: [] }]} />);
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  expect(screen.getByText("No service URLs")).toBeInTheDocument();
});
