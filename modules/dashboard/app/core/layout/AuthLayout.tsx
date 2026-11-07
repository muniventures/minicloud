import { Outlet } from "react-router-dom";
import { GuestArea } from "../auth/GuestArea";

export function AuthLayout() {
  return (
    <GuestArea>
      <main className="flex min-h-screen items-center justify-center bg-background px-4 py-10">
        <Outlet />
      </main>
    </GuestArea>
  );
}
