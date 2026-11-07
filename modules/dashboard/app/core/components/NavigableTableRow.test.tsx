import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell } from "@/components/ui/table";
import { NavigableTableRow } from "./NavigableTableRow";

afterEach(cleanup);

describe("NavigableTableRow", () => {
  it("navigates from non-interactive cells but leaves embedded actions independent", () => {
    const action = vi.fn();
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route
            path="/"
            element={
              <Table><TableBody><NavigableTableRow href="/records/1">
                <TableCell>Record one</TableCell>
                <TableCell><Button onClick={action}>Copy</Button></TableCell>
              </NavigableTableRow></TableBody></Table>
            }
          />
          <Route path="/records/1" element={<p>Record detail</p>} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(action).toHaveBeenCalledOnce();
    expect(screen.queryByText("Record detail")).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Record one"));
    expect(screen.getByText("Record detail")).toBeInTheDocument();
  });
});
