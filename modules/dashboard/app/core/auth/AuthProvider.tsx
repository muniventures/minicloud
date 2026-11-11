import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type PropsWithChildren } from "react";
import { getAccountCapabilities, getAccountSession, logoutAccount, type AccountCapabilities } from "../../api/accountApi";
import { setCsrfToken, subscribeAuthFailures } from "../../api/apiClient";
import { getMe } from "../../api/identityApi";
import { ApiError, type MeResponse } from "../../models/controlPlane";

export type AuthStatus = "initializing" | "authenticated" | "unauthenticated" | "unavailable" | "setup_required" | "setup_blocked";
type AuthContextValue = {
  status: AuthStatus;
  profile: MeResponse | null;
  capabilities: AccountCapabilities | null;
  selectedOrganization: MeResponse["organizations"][number] | null;
  selectOrganization: (organizationId: string) => boolean;
  profileError: string | null;
  refreshSession: () => Promise<void>;
  refreshProfile: () => Promise<void>;
  logout: () => Promise<void>;
};

export const AuthContext = createContext<AuthContextValue>({
  status: "initializing", profile: null, capabilities: null, selectedOrganization: null, selectOrganization: () => false,
  profileError: null, refreshSession: async () => {}, refreshProfile: async () => {}, logout: async () => {}
});

export function AuthProvider({ children }: PropsWithChildren) {
  const [status, setStatus] = useState<AuthStatus>("initializing");
  const [profile, setProfile] = useState<MeResponse | null>(null);
  const [capabilities, setCapabilities] = useState<AccountCapabilities | null>(null);
  const [profileError, setProfileError] = useState<string | null>(null);
  const [selectedOrganizationId, setSelectedOrganizationId] = useState<string | null>(null);
  const generation = useRef(0);

  const refreshSession = useCallback(async () => {
    const current = ++generation.current;
    setProfileError(null);
    try {
      const nextCapabilities = await getAccountCapabilities();
      if (current !== generation.current) return;
      setCapabilities(nextCapabilities);
      if (nextCapabilities.bootstrapState !== "ready") {
        setCsrfToken(null);
        setProfile(null);
        setStatus(nextCapabilities.bootstrapState === "required" ? "setup_required" : "setup_blocked");
        return;
      }
      const session = await getAccountSession();
      if (current !== generation.current) return;
      setCsrfToken(session.csrfToken);
      const nextProfile = await getMe();
      if (current !== generation.current) return;
      setProfile(nextProfile);
      setStatus("authenticated");
    } catch (error) {
      if (current !== generation.current) return;
      if (error instanceof ApiError && error.status === 401) {
        setCsrfToken(null);
        setProfile(null);
        setStatus("unauthenticated");
      } else {
        setStatus("unavailable");
        setProfileError("Unable to load your account. Retry when the service is available.");
      }
    }
  }, []);

  useEffect(() => {
    const unsubscribe = subscribeAuthFailures(code => {
      ++generation.current;
      if (code === 401) {
        setCsrfToken(null);
        setProfile(null);
        setProfileError(null);
        setStatus("unauthenticated");
      } else {
        setStatus("unavailable");
        setProfileError("Account service is temporarily unavailable.");
      }
    });
    void refreshSession();
    return () => { ++generation.current; unsubscribe(); };
  }, [refreshSession]);

  const logout = useCallback(async () => {
    ++generation.current;
    try {
      await logoutAccount();
      setCsrfToken(null);
      setProfile(null);
      setStatus("unauthenticated");
      setProfileError(null);
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setCsrfToken(null);
        setProfile(null);
        setStatus("unauthenticated");
      } else {
        setStatus("unavailable");
        setProfileError("Sign out could not be confirmed. Retry account access.");
      }
    }
  }, []);

  // Only loaded memberships can be selected; an unknown or revoked selection falls back to the first membership.
  const organizations = profile?.organizations;
  const selectOrganization = useCallback((organizationId: string) => {
    if (!organizations?.some(organization => organization.id === organizationId)) return false;
    setSelectedOrganizationId(organizationId);
    return true;
  }, [organizations]);
  const selectedOrganization = organizations?.find(organization => organization.id === selectedOrganizationId)
    ?? organizations?.[0] ?? null;

  const value = useMemo(() => ({
    status, profile, capabilities, selectedOrganization, selectOrganization,
    profileError, refreshSession, refreshProfile: refreshSession, logout
  }), [status, profile, capabilities, selectedOrganization, selectOrganization, profileError, refreshSession, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  return useContext(AuthContext);
}
