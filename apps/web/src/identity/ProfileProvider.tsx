import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useUsers, type User } from '../api/v2/household.ts';
import { getActiveProfileId, setActiveProfileId } from './profileStore.ts';

export interface ProfileContextValue {
  status: 'loading' | 'error' | 'ready';
  /** Active users, in the order the server returns them. */
  activeUsers: User[];
  /** The chosen profile, or null when none is chosen (or it is no longer active). */
  profile: User | null;
  selectProfile(id: string): void;
  clearProfile(): void;
}

const ProfileContext = createContext<ProfileContextValue | null>(null);

export function ProfileProvider({ children }: { children: ReactNode }) {
  const usersQuery = useUsers();
  const [profileId, setProfileId] = useState<string | null>(() => getActiveProfileId());

  const activeUsers = useMemo(() => (usersQuery.data ?? []).filter((u) => u.active), [usersQuery.data]);
  const profile = activeUsers.find((u) => u.id === profileId) ?? null;

  const selectProfile = useCallback((id: string) => {
    setActiveProfileId(id);
    setProfileId(id);
  }, []);

  const clearProfile = useCallback(() => {
    setActiveProfileId(null);
    setProfileId(null);
  }, []);

  // A stored profile that no longer exists or was deactivated sends the user back to the picker.
  useEffect(() => {
    if (usersQuery.data && profileId && !profile) clearProfile();
  }, [usersQuery.data, profileId, profile, clearProfile]);

  const status = usersQuery.isPending ? 'loading' : usersQuery.isError ? 'error' : 'ready';
  const value = useMemo<ProfileContextValue>(
    () => ({ status, activeUsers, profile, selectProfile, clearProfile }),
    [status, activeUsers, profile, selectProfile, clearProfile],
  );

  return <ProfileContext.Provider value={value}>{children}</ProfileContext.Provider>;
}

export function useProfile(): ProfileContextValue {
  const value = useContext(ProfileContext);
  if (!value) throw new Error('useProfile must be used inside <ProfileProvider>');
  return value;
}
