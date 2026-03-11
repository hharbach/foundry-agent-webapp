import { Tag } from '@fluentui/react-components';
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
        <Tag
          key={i}
          role="listitem"
          dismissible
          dismissIcon={{ 'aria-label': 'Remove queued message' }}
          onDismiss={() => onRemove(i)}
          size="small"
          shape="circular"
        >
          {text.length > 60 ? text.slice(0, 60) + '…' : text}
        </Tag>
      ))}
    </div>
  );
};
