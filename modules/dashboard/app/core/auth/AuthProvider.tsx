import { onAuthStateChanged, type User } from "firebase/auth";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type PropsWithChildren } from "react";
import { getMe } from "../../api/identityApi";
import type { MeResponse } from "../../models/controlPlane";
import { auth } from "./firebase";

type AuthContextValue = {
  user: User | null;
  firebaseUser: User | null;
  loading: boolean;
  isInitializing: boolean;
  isProfileLoading: boolean;
  profile: MeResponse | null;
  selectedOrganization: MeResponse["organizations"][number] | null;
  profileError: string | null;
  refreshProfile: () => Promise<void>;
};

export const AuthContext = createContext<AuthContextValue>({
  user: null,
  firebaseUser: null,
  loading: true,
  isInitializing: true,
  isProfileLoading: false,
  profile: null,
  selectedOrganization: null,
  profileError: null,
  refreshProfile: async () => {}
});

export function AuthProvider({ children }: PropsWithChildren) {
  const [user, setUser] = useState<User | null>(auth.currentUser);
  const [loading, setLoading] = useState(true);
  const [profile, setProfile] = useState<MeResponse | null>(null);
  const [isProfileLoading, setIsProfileLoading] = useState(false);
  const [profileError, setProfileError] = useState<string | null>(null);

  useEffect(() => {
    return onAuthStateChanged(auth, nextUser => {
      setUser(nextUser);
      setLoading(false);
    });
  }, []);

  const refreshProfile = useCallback(async () => {
    if (!user) {
      setProfile(null);
      setProfileError(null);
      setIsProfileLoading(false);
      return;
    }

    setIsProfileLoading(true);
    setProfileError(null);
    try {
      setProfile(await getMe());
    } catch (error) {
      setProfileError(error instanceof Error ? error.message : "Unable to load workspace.");
    } finally {
      setIsProfileLoading(false);
    }
  }, [user]);

  useEffect(() => {
    if (!user) {
      setProfile(null);
      setProfileError(null);
      setIsProfileLoading(false);
      return;
    }

    setIsProfileLoading(true);
    setProfileError(null);

    getMe()
      .then(setProfile)
      .catch(error => setProfileError(error instanceof Error ? error.message : "Unable to load workspace."))
      .finally(() => setIsProfileLoading(false));
  }, [user]);

  const value = useMemo(
    () => ({
      user,
      firebaseUser: user,
      loading,
      isInitializing: loading,
      isProfileLoading,
      profile,
      selectedOrganization: profile?.organizations[0] ?? null,
      profileError,
      refreshProfile
    }),
    [isProfileLoading, loading, profile, profileError, refreshProfile, user]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  return useContext(AuthContext);
}
