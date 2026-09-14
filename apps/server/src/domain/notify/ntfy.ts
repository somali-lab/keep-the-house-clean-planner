import { postNotification, type Notifier, type NotifierOptions, type NotifyMessage } from './notifier.ts';

/** HTTP header values must be ASCII; names and Dutch text go in the UTF-8 body. */
const asciiHeader = (value: string) => value.normalize('NFKD').replace(/[^\x20-\x7e]/g, '');

/** ntfy: plain-text POST to the topic URL (NOTIFY_URL), optional bearer token. */
export class NtfyNotifier implements Notifier {
  readonly type = 'ntfy' as const;
  private readonly options: NotifierOptions;

  constructor(options: NotifierOptions) {
    this.options = options;
  }

  send(message: NotifyMessage): Promise<void> {
    return postNotification(this.type, this.options, {
      headers: {
        'Content-Type': 'text/plain; charset=utf-8',
        Title: asciiHeader(message.title),
        Tags: 'broom',
      },
      body: message.body,
    });
  }
}
