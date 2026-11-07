import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { RecordToolbar } from "./RecordToolbar";

afterEach(cleanup);

describe("RecordToolbar", () => {
  it("renders record controls without collection-only controls or an empty More menu", () => {
    render(
      <MemoryRouter>
        <RecordToolbar
          title="Demo"
          back={{ to: "/apps", label: "Back to apps" }}
          status={<span>Active</span>}
          metadata={<span>demo · p0</span>}
          refreshLabel="Refresh Demo"
          onRefresh={() => {}}
          secondary={{ id: "history", label: "History", onClick: () => {} }}
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole("heading", { name: "Demo" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to apps" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh Demo" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "More" })).not.toBeInTheDocument();
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByText("Filter & sort")).not.toBeInTheDocument();
  });

  it("deduplicates refresh and puts additional actions in More", async () => {
    let complete!: () => void;
    const request = new Promise<void>((resolve) => { complete = resolve; });
    const refresh = vi.fn(() => request);
    const remove = vi.fn();
    render(
      <MemoryRouter>
        <RecordToolbar
          title="Demo"
          refreshLabel="Refresh Demo"
          onRefresh={refresh}
          more={[[{ id: "remove", label: "Remove", onClick: remove }]]}
        />
      </MemoryRouter>,
    );

    const refreshButton = screen.getByRole("button", { name: "Refresh Demo" });
    fireEvent.click(refreshButton);
    fireEvent.click(refreshButton);
    expect(refresh).toHaveBeenCalledOnce();
    expect(refreshButton).toBeDisabled();
    fireEvent.pointerDown(screen.getByRole("button", { name: "More" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Remove" }));
    expect(remove).toHaveBeenCalledOnce();
    await act(async () => { complete(); await request; });
    expect(refreshButton).toBeEnabled();
  });
});
