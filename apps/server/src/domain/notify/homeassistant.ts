import { postNotification, type Notifier, type NotifierOptions, type NotifyMessage } from './notifier.ts';

/** Home Assistant: JSON POST to a webhook URL; the automation decides what to do with it. */
export class HomeAssistantNotifier implements Notifier {
  readonly type = 'homeassistant' as const;
  private readonly options: NotifierOptions;

  constructor(options: NotifierOptions) {
    this.options = options;
  }

  send(message: NotifyMessage): Promise<void> {
    return postNotification(this.type, this.options, {
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: message.title, message: message.body, ...message.data }),
    });
  }
}
