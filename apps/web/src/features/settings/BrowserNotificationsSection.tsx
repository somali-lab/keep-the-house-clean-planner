import { isTimeOfDay } from '@/lib/timeOfDay';
import { Bell, BellOff, BellRing, Clock, Plus, Save, X, type LucideIcon } from 'lucide-react';
import { useEffect, useId, useState, type FormEvent } from 'react';
import { NativeSelect } from '@/components/NativeSelect';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { isStaleEntity } from '../../api/index.ts';
import { useSettings, type User } from '../../api/v2/household.ts';
import { FALLBACK_LIMITS, useLimits } from '../../api/v2/queries.ts';
import { format, t, type MessageKey } from '../../i18n/nl.ts';
import { useProfile } from '../../identity/index.ts';
import { useSaveBrowserNotifications } from '../notifications/api.ts';
import {
  getPermissionState,
  requestPermission,
  showNotification,
  type PermissionState,
} from '../notifications/browserNotification.ts';
import { Field, FormActions, FormMessage, SettingsCardHeader, checkboxClass, listRowClass, settingsCardClass } from './SettingsCard.tsx';

/** Page for everyone: a person's own browser-notification moments, and the permission of this device. */
export function BrowserNotificationsPage() {
  return (
    <section>
      <PageHeader title={t('settings.notifications.title')} description={t('settings.notifications.help')} />
      <BrowserNotificationsSection />
    </section>
  );
}

export function BrowserNotificationsSection() {
  const idPrefix = useId();
  const { profile, activeUsers } = useProfile();
  const settings = useSettings();
  const maxTimes = (useLimits().data?.notifications ?? FALLBACK_LIMITS.notifications).maxBrowserTimes;
  const [personId, setPersonId] = useState<string | null>(null);
  if (!profile) return null;

  const isAdmin = profile.role === 'admin';
  const person = (isAdmin ? activeUsers.find((user) => user.id === personId) : undefined) ?? profile;

  return (
    <div className="flex max-w-3xl flex-col gap-6">
      <section className={settingsCardClass} aria-labelledby={`${idPrefix}-times-title`}>
        <SettingsCardHeader
          icon={<Clock aria-hidden="true" />}
          titleId={`${idPrefix}-times-title`}
          title={t('settings.notifications.times')}
          description={format('settings.notifications.timesHelp', {
            timezone: settings.data?.timezone ?? '',
            max: maxTimes,
          })}
        />
        {isAdmin && (
          <Field>
            <Label htmlFor={`${idPrefix}-person`}>{t('settings.notifications.person')}</Label>
            <NativeSelect id={`${idPrefix}-person`} value={person.id} onChange={(event) => setPersonId(event.target.value)}>
              {activeUsers.map((user) => (
                <option key={user.id} value={user.id}>
                  {user.name}
                </option>
              ))}
            </NativeSelect>
          </Field>
        )}
        <MomentsForm key={person.id} user={person} maxTimes={maxTimes} />
        <p className="text-sm text-muted-foreground">{t('settings.notifications.separate')}</p>
      </section>
      <DevicePermission />
    </div>
  );
}

function MomentsForm({ user, maxTimes }: { user: User; maxTimes: number }) {
  const idPrefix = useId();
  const { profile } = useProfile();
  const save = useSaveBrowserNotifications();
  const stored = user.browserNotifications;
  const [enabled, setEnabled] = useState(stored.enabled);
  const [times, setTimes] = useState<string[]>([...stored.times].sort());
  const [draft, setDraft] = useState('');
  const [draftError, setDraftError] = useState<MessageKey | null>(null);
  const errorId = `${idPrefix}-new-error`;

  /** The list with the pending draft added, or null (and an error shown) when the draft cannot be added. */
  const withDraft = (): string[] | null => {
    const problem: MessageKey | null = !isTimeOfDay(draft)
      ? 'settings.notifications.timeInvalid'
      : times.includes(draft)
        ? 'settings.notifications.timeDuplicate'
        : times.length >= maxTimes
          ? 'settings.notifications.timeMax'
          : null;
    setDraftError(problem);
    return problem ? null : [...times, draft].sort();
  };

  const addTime = () => {
    save.reset();
    const next = withDraft();
    if (!next) return;
    setDraftError(null);
    setTimes(next);
    setDraft('');
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    let toSave = times;
    // A time that was typed but not added yet is part of what the person sees, so save it too.
    if (draft !== '') {
      const next = withDraft();
      if (!next) return;
      toSave = next;
      setTimes(next);
      setDraft('');
      setDraftError(null);
    }
    save.mutate({ user, settings: { enabled, times: toSave } });
  };

  const savedMessage =
    user.id === profile?.id ? t('settings.saved') : format('settings.notifications.savedFor', { name: user.name });

  return (
    <form className="flex flex-col gap-4" onSubmit={submit} noValidate aria-label={t('settings.notifications.times')}>
      <label className="flex w-fit cursor-pointer items-center gap-2 text-sm font-semibold">
        <input
          type="checkbox"
          className={checkboxClass}
          checked={enabled}
          onChange={(event) => {
            save.reset();
            setEnabled(event.target.checked);
          }}
        />
        {t('settings.notifications.enabled')}
      </label>
      {times.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('settings.notifications.timesNone')}</p>
      ) : (
        <ul className="flex flex-wrap gap-2" aria-label={t('settings.notifications.chosenTimes')}>
          {times.map((time) => (
            <li key={time} className={`${listRowClass} gap-2 py-1.5 pr-2`}>
              <span className="font-semibold tabular-nums">{time}</span>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                aria-label={format('settings.notifications.removeTime', { time })}
                onClick={() => {
                  save.reset();
                  setTimes(times.filter((value) => value !== time));
                }}
              >
                <X aria-hidden="true" />
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-wrap items-end gap-3">
        <Field className="w-40">
          <Label htmlFor={`${idPrefix}-new`}>{t('settings.notifications.newTime')}</Label>
          <Input
            id={`${idPrefix}-new`}
            type="time"
            className="h-10 bg-card"
            value={draft}
            aria-invalid={draftError ? true : undefined}
            aria-describedby={draftError ? errorId : undefined}
            onChange={(event) => {
              save.reset();
              setDraft(event.target.value);
              setDraftError(null);
            }}
            onKeyDown={(event) => {
              // Enter adds the time; it must not submit (save) the whole form.
              if (event.key === 'Enter') {
                event.preventDefault();
                addTime();
              }
            }}
          />
        </Field>
        <Button type="button" variant="outline" onClick={addTime} disabled={times.length >= maxTimes}>
          <Plus aria-hidden="true" />
          {t('settings.notifications.addTime')}
        </Button>
      </div>
      {draftError && (
        <div id={errorId}>
          <FormMessage kind="alert">{format(draftError, { max: maxTimes })}</FormMessage>
        </div>
      )}
      {save.isError && <FormMessage kind="alert">{isStaleEntity(save.error) ? t('app.staleEntity') : t('app.error')}</FormMessage>}
      {save.isSuccess && <FormMessage kind="status">{savedMessage}</FormMessage>}
      <FormActions>
        <Button type="submit" disabled={save.isPending}>
          <Save aria-hidden="true" />
          {t('common.save')}
        </Button>
      </FormActions>
    </form>
  );
}

const STATE_ICON: Record<PermissionState, LucideIcon> = {
  default: BellRing,
  granted: Bell,
  denied: BellOff,
  unsupported: BellOff,
  insecure: BellOff,
};

/** Permission is per browser and device; this card shows it and lets the person grant it and try it out. */
function DevicePermission() {
  const idPrefix = useId();
  const [state, setState] = useState<PermissionState>(() => getPermissionState());
  const [test, setTest] = useState<'sent' | 'failed' | null>(null);

  // The person may change the permission in the browser's own settings while this page is open.
  useEffect(() => {
    const refresh = () => setState(getPermissionState());
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', refresh);
    return () => {
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, []);

  const Icon = STATE_ICON[state];
  return (
    <section className={settingsCardClass} aria-labelledby={`${idPrefix}-title`}>
      <SettingsCardHeader
        icon={<Icon aria-hidden="true" />}
        titleId={`${idPrefix}-title`}
        title={t('settings.notifications.permission.title')}
        description={t(`settings.notifications.permission.help.${state}` as MessageKey)}
      />
      <p className="text-sm font-semibold" aria-live="polite">
        {format('settings.notifications.permission.status', {
          state: t(`settings.notifications.permission.${state}` as MessageKey),
        })}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          disabled={state !== 'default'}
          onClick={async () => setState(await requestPermission())}
        >
          <BellRing aria-hidden="true" />
          {t('settings.notifications.permission.request')}
        </Button>
        <Button
          type="button"
          variant="secondary"
          disabled={state !== 'granted'}
          onClick={async () => {
            const shown = await showNotification({
              title: t('notify.browser.testTitle'),
              body: t('notify.browser.testBody'),
            });
            setTest(shown ? 'sent' : 'failed');
          }}
        >
          <Bell aria-hidden="true" />
          {t('settings.notifications.permission.test')}
        </Button>
      </div>
      {/* Always rendered, so screen readers announce a result that appears in it. */}
      <div aria-live="polite">
        {test && (
          <FormMessage kind={test === 'sent' ? 'status' : 'alert'}>
            {t(test === 'sent' ? 'settings.notifications.permission.testSent' : 'settings.notifications.permission.testFailed')}
          </FormMessage>
        )}
      </div>
    </section>
  );
}
