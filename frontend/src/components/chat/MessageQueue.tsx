import { Button, Text } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import styles from './MessageQueue.module.css';

interface MessageQueueProps {
  messages: string[];
  onRemove: (index: number) => void;
}

export const MessageQueue: React.FC<MessageQueueProps> = ({ messages, onRemove }) => {
  if (messages.length === 0) return null;

  return (
    <div className={styles.queue} role="list" aria-label="Queued messages">
      <span className={styles.label}>Queued:</span>
      {messages.map((text, i) => (
        <span key={i} role="listitem" className={styles.chip}>
          <Text size={200}>{text.length > 60 ? text.slice(0, 60) + '…' : text}</Text>
          <Button
            appearance="transparent"
            icon={<DismissRegular />}
            size="small"
            onClick={() => onRemove(i)}
            aria-label="Remove queued message"
            className={styles.dismissButton}
          />
        </span>
      ))}
    </div>
  );
};
