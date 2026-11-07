import { describe, expect, it, afterEach, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { CollectionToolbar } from "./CollectionToolbar";
import { compareRecords } from "./collectionQuery";
afterEach(cleanup);
describe("collection contract", () => {
  it("keeps unknown values last in both directions and breaks ties by id", () => {
    for (const direction of ["asc", "desc"]) {
      expect(compareRecords(null, "known", direction, "a", "b")).toBe(1);
      expect(compareRecords("same", "same", direction, "a", "b")).toBeLessThan(
        0,
      );
      expect(compareRecords(NaN, 3, direction, "a", "b")).toBe(1);
    }
  });
  it("disables refresh until the current request completes", async () => {
    let complete!: () => void;
    const request = new Promise<void>((resolve) => {
      complete = resolve;
    });
    const refresh = vi.fn(() => request);
    render(
      <CollectionToolbar
        label="records"
        query={{
          search: "",
          filters: {},
          sort: "name",
          direction: "asc",
          view: "cards",
        }}
        onChange={() => {}}
        onRefresh={refresh}
        count={0}
        sorts={[]}
      />,
    );
    const button = screen.getByRole("button", { name: "Refresh records" });
    fireEvent.click(button);
    fireEvent.click(button);
    expect(button).toBeDisabled();
    expect(refresh).toHaveBeenCalledTimes(1);
    await act(async () => {
      complete();
      await request;
    });
    expect(button).toBeEnabled();
  });
  it("omits empty and invisible overflow actions", () => {
    render(
      <CollectionToolbar
        label="records"
        query={{
          search: "",
          filters: {},
          sort: "name",
          direction: "asc",
          view: "cards",
        }}
        onChange={() => {}}
        onRefresh={() => {}}
        count={0}
        sorts={[{ value: "name", label: "Name" }]}
        more={[
          [
            {
              id: "hidden",
              label: "Hidden",
              visible: false,
              onClick: () => {},
            },
          ],
        ]}
      />,
    );
    expect(
      screen.queryByRole("button", { name: "More" }),
    ).not.toBeInTheDocument();
  });
  it("contains additional actions only and does not duplicate primary", async () => {
    const action = { id: "create", label: "Create", onClick: () => {} };
    render(
      <CollectionToolbar
        label="records"
        query={{
          search: "",
          filters: {},
          sort: "name",
          direction: "asc",
          view: "cards",
        }}
        onChange={() => {}}
        onRefresh={() => {}}
        count={0}
        sorts={[{ value: "name", label: "Name" }]}
        primary={action}
        more={[[action, { id: "export", label: "Export", onClick: () => {} }]]}
      />,
    );
    fireEvent.pointerDown(screen.getByRole("button", { name: "More" }), {
      button: 0,
      ctrlKey: false,
    });
    expect(
      await screen.findByRole("menuitem", { name: "Export" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("menuitem", { name: "Create" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("menuitem", { name: /sort|filter/i }),
    ).not.toBeInTheDocument();
  });
  it("keeps the filter popover open for edits and returns focus on Escape", async () => {
    const onChange = vi.fn();
    render(
      <CollectionToolbar
        label="records"
        query={{ search: "", filters: {}, sort: "name", direction: "asc", view: "cards" }}
        onChange={onChange}
        onRefresh={() => {}}
        count={2}
        filters={[{ id: "state", label: "State", options: [{ value: "active", label: "Active" }] }]}
        sorts={[{ value: "name", label: "Name" }]}
      />,
    );
    const trigger = screen.getByRole("button", { name: "Filter & sort" });
    fireEvent.click(trigger);
    fireEvent.click(screen.getByRole("button", { name: "Descending" }));
    expect(onChange).toHaveBeenCalled();
    expect(screen.getByText("2 matching records")).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByText("2 matching records")).not.toBeInTheDocument();
    await waitFor(() => expect(trigger).toHaveFocus());
  });
});
