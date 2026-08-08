import React, { useCallback, useEffect, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds } from 'Helpers/Props';
import {
  cancelAllCommands,
  fetchCommands,
  pauseAllCommands,
  resumeAllCommands,
} from 'Store/Actions/commandActions';
import translate from 'Utilities/String/translate';
import QueuedTaskRow from './QueuedTaskRow';
import styles from './QueuedTasks.css';

const columns = [
  {
    name: 'trigger',
    label: '',
    isVisible: true,
  },
  {
    name: 'commandName',
    label: () => translate('Name'),
    isVisible: true,
  },
  {
    name: 'queued',
    label: () => translate('Queued'),
    isVisible: true,
  },
  {
    name: 'started',
    label: () => translate('Started'),
    isVisible: true,
  },
  {
    name: 'ended',
    label: () => translate('Ended'),
    isVisible: true,
  },
  {
    name: 'duration',
    label: () => translate('Duration'),
    isVisible: true,
  },
  {
    name: 'actions',
    isVisible: true,
  },
];

export default function QueuedTasks() {
  const dispatch = useDispatch();
  const { isFetching, isPopulated, items } = useSelector(
    (state: AppState) => state.commands
  );
  const [isCancelAllConfirmOpen, setIsCancelAllConfirmOpen] = useState(false);

  useEffect(() => {
    dispatch(fetchCommands());
  }, [dispatch]);

  const onPauseAllPress = useCallback(() => {
    dispatch(pauseAllCommands());
  }, [dispatch]);

  const onCancelAllPress = useCallback(() => {
    setIsCancelAllConfirmOpen(true);
  }, []);

  const onCancelAllConfirmed = useCallback(() => {
    setIsCancelAllConfirmOpen(false);
    dispatch(cancelAllCommands());
  }, [dispatch]);

  const onCancelAllCancelled = useCallback(() => {
    setIsCancelAllConfirmOpen(false);
  }, []);

  const onResumeAllPress = useCallback(() => {
    dispatch(resumeAllCommands());
  }, [dispatch]);

  const hasActiveTasks = items.some(
    (item) =>
      item.status === 'started' ||
      item.status === 'queued' ||
      item.status === 'paused'
  );

  const hasRunningTasks = items.some((item) => item.status === 'started');
  const hasPausedTasks = items.some((item) => item.status === 'paused');

  return (
    <FieldSet legend={translate('Queue')}>
      {isPopulated && hasActiveTasks && (
        <div className={styles.buttons}>
          <Button onPress={onPauseAllPress} isDisabled={!hasRunningTasks}>
            {translate('PauseAll')}
          </Button>
          <Button onPress={onResumeAllPress} isDisabled={!hasPausedTasks}>
            {translate('ResumeAll')}
          </Button>
          <Button onPress={onCancelAllPress} kind="danger">
            {translate('CancelAll')}
          </Button>
        </div>
      )}

      {isFetching && !isPopulated && <LoadingIndicator />}

      {isPopulated && (
        <Table columns={columns}>
          <TableBody>
            {items.map((item) => {
              return <QueuedTaskRow key={item.id} {...item} />;
            })}
          </TableBody>
        </Table>
      )}

      <ConfirmModal
        isOpen={isCancelAllConfirmOpen}
        kind={kinds.DANGER}
        title={translate('CancelAll')}
        message={
          <div>
            <div>{translate('CancelAllQueuedAndRunningTasks')}</div>
            <div>{translate('ThisCannotBeUndone')}</div>
          </div>
        }
        confirmLabel={translate('CancelAll')}
        onConfirm={onCancelAllConfirmed}
        onCancel={onCancelAllCancelled}
      />
    </FieldSet>
  );
}
