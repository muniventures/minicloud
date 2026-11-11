import { ChevronsUpDown, Code, GalleryVerticalEnd, LogOut, Settings } from "lucide-react";
import { runtimeConfig } from "@/core/config/runtimeConfig";
import { NavLink } from "react-router-dom";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "../auth/AuthProvider";
import { dashboardRoutes, navigationRoutes, settingsRoutes } from "../routing/dashboardRoutes";

function getFallbackName(email?: string | null) {
  const localPart = email?.split("@")[0];
  if (!localPart) {
    return "Account";
  }

  return localPart
    .split(/[._-]+/)
    .filter(Boolean)
    .map((part) => `${part[0].toUpperCase()}${part.slice(1)}`)
    .join(" ");
}

function getInitials(name: string) {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0].toUpperCase())
    .join("");
}

export function SidebarNavigation({ onNavigate }: { onNavigate: () => void }) {
  const { selectedOrganization, profile, logout } = useAuth();
  const email = profile?.email;
  const userName = profile?.displayName || getFallbackName(email);

  return (
    <div className="flex h-full flex-col bg-sidebar text-sidebar-foreground">
      <div className="p-2">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              className="flex h-12 w-full items-center gap-2 rounded-lg p-2 text-left text-sm outline-none transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring data-[state=open]:bg-sidebar-accent data-[state=open]:text-sidebar-accent-foreground"
            >
              <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-sidebar-primary text-sidebar-primary-foreground">
                <GalleryVerticalEnd className="size-4" aria-hidden="true" />
              </span>
              <span className="grid min-w-0 flex-1 text-left text-sm leading-tight">
                <span className="truncate font-semibold">Minicloud</span>
                <span className="truncate text-xs">{selectedOrganization?.name || "Workspace"}</span>
              </span>
              <ChevronsUpDown className="ml-auto size-4 shrink-0" aria-hidden="true" />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent
            side="bottom"
            align="start"
            sideOffset={4}
            className="w-[--radix-dropdown-menu-trigger-width] min-w-56 rounded-lg"
          >
            <DropdownMenuLabel className="text-xs text-muted-foreground">Workspace</DropdownMenuLabel>
            <DropdownMenuItem className="gap-2 p-2" onSelect={(event) => event.preventDefault()}>
              <span className="flex size-6 items-center justify-center rounded-sm border">
                <GalleryVerticalEnd className="size-3.5" aria-hidden="true" />
              </span>
              <span className="min-w-0 flex-1 truncate">{selectedOrganization?.name || "Workspace"}</span>
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto">
        {[navigationRoutes, settingsRoutes].map((routes, index) => (
          <nav
            key={index}
            aria-label={index ? "Workspace administration" : "Primary"}
            className="relative flex w-full min-w-0 flex-col p-2"
          >
            <p className="flex h-8 shrink-0 items-center rounded-md px-2 text-xs font-medium text-sidebar-foreground/70">
              {index ? "Workspace administration" : "Platform"}
            </p>
            <div className="w-full text-sm">
              <div className="flex w-full min-w-0 flex-col gap-1">
                {routes.map((route) => {
                  const Icon = route.nav?.icon;
                  return (
                    <NavLink
                      key={route.key}
                      to={route.path}
                      onClick={onNavigate}
                      className={({ isActive }) =>
                        `flex h-8 w-full items-center gap-2 overflow-hidden rounded-md p-2 text-left text-sm outline-none transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring ${isActive ? "bg-sidebar-accent font-medium text-sidebar-accent-foreground" : "text-sidebar-foreground"}`
                      }
                    >
                      {Icon ? <Icon className="size-4 shrink-0" /> : null}
                      <span className="truncate">{route.nav?.label}</span>
                    </NavLink>
                  );
                })}
              </div>
            </div>
          </nav>
        ))}
      </div>
      <div className="mt-auto p-2">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              className="flex min-h-12 w-full items-center gap-2 rounded-md px-2 text-left text-sm outline-none transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring data-[state=open]:bg-sidebar-accent data-[state=open]:text-sidebar-accent-foreground"
            >
              <span className="flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-sidebar-primary text-xs font-semibold text-sidebar-primary-foreground">
                <span aria-hidden="true">{getInitials(userName)}</span>
              </span>
              <span className="grid min-w-0 flex-1 text-left text-sm leading-tight">
                <span className="truncate font-semibold">{userName}</span>
                <span className="truncate text-xs text-sidebar-foreground/70">{email}</span>
              </span>
              <ChevronsUpDown className="size-4 shrink-0 text-sidebar-foreground/70" aria-hidden="true" />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent
            side="top"
            align="end"
            sideOffset={8}
            className="w-[--radix-dropdown-menu-trigger-width] min-w-56 rounded-lg"
          >
            <DropdownMenuLabel className="flex items-center gap-2 p-1.5 font-normal">
              <span className="flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-primary text-xs font-semibold text-primary-foreground">
                <span aria-hidden="true">{getInitials(userName)}</span>
              </span>
              <span className="grid min-w-0 flex-1 text-left text-sm leading-tight">
                <span className="truncate font-semibold">{userName}</span>
                <span className="truncate text-xs text-muted-foreground">{email}</span>
              </span>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem asChild>
              <NavLink to={dashboardRoutes.settings.path} onClick={onNavigate}>
                <Settings aria-hidden="true" />
                Settings
              </NavLink>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <a href={runtimeConfig.sourceUrl}>
                <Code aria-hidden="true" />
                Source code
              </a>
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => void logout()}>
              <LogOut aria-hidden="true" />
              Log out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </div>
  );
}
