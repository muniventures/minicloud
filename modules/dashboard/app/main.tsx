import { ThemeProvider } from "next-themes";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import "./styles.css";
import { AuthProvider } from "./core/auth/AuthProvider";
import { routes } from "./routes";

const router = createBrowserRouter(routes);
const root = document.getElementById("root");

if (!root) {
  throw new Error("Dashboard root element was not found.");
}

createRoot(root).render(
  <StrictMode>
    <ThemeProvider attribute="class" defaultTheme="system" enableSystem><AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider></ThemeProvider>
  </StrictMode>
);
