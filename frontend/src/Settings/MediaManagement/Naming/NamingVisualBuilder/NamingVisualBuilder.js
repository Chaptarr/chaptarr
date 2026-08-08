import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { DndContext, DragOverlay, closestCenter } from '@dnd-kit/core';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalContent from 'Components/Modal/ModalContent';
import ModalHeader from 'Components/Modal/ModalHeader';
import ModalBody from 'Components/Modal/ModalBody';
import ModalFooter from 'Components/Modal/ModalFooter';
import Button from 'Components/Link/Button';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import TokenPalette from './TokenPalette';
import NamingCanvas from './NamingCanvas';
import ValidationPanel from './ValidationPanel';
import ToolBar from './ToolBar';
import { createId, validateAst } from './utils';
import styles from './NamingVisualBuilder.css';

class NamingVisualBuilder extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      ast: {
        nodesById: {},
        rootIds: []
      },
      draggedItem: null,
      validation: { isValid: true, errors: [] },
      isValidating: false
    };

    this.validationTimeout = null;
    this.previewTimeout = null;
  }

  componentDidMount() {
    if (this.props.isOpen) {
      this.loadPatternFromProps();
    }
  }

  componentDidUpdate(prevProps) {
    if (!prevProps.isOpen && this.props.isOpen) {
      this.loadPatternFromProps();
    }
  }

  componentWillUnmount() {
    if (this.validationTimeout) {
      clearTimeout(this.validationTimeout);
    }
    if (this.previewTimeout) {
      clearTimeout(this.previewTimeout);
    }
  }

  loadPatternFromProps = async () => {
    const { initialPattern } = this.props;
    
    if (!initialPattern) {
      this.setState({
        ast: { nodesById: {}, rootIds: [] }
      });
      return;
    }

    try {
      const response = await fetch('/api/v1/config/naming-pattern/decompile', {
        method: 'POST',
        headers: { 
          'Content-Type': 'application/json',
          'X-Api-Key': window.Chaptarr.apiKey
        },
        body: JSON.stringify({ pattern: initialPattern })
      });

      if (response.ok) {
        const { ast } = await response.json();
        this.setState({ ast });
        this.scheduleValidation();
      }
    } catch (error) {
      console.error('Failed to decompile pattern:', error);
    }
  };

  handleSave = async () => {
    const { onSave } = this.props;
    const { ast } = this.state;

    const clientValidation = validateAst(ast);
    if (!clientValidation.isValid) {
      this.setState({ validation: clientValidation });
      return;
    }

    try {
      const response = await fetch('/api/v1/config/naming-pattern/compile', {
        method: 'POST',
        headers: { 
          'Content-Type': 'application/json',
          'X-Api-Key': window.Chaptarr.apiKey
        },
        body: JSON.stringify({ ast })
      });

      if (response.ok) {
        const { pattern } = await response.json();
        onSave(pattern);
      } else {
        let errorMessage = translate('NamingBuilderCompileRequestFailed');
        try {
          const error = await response.json();
          errorMessage = error?.error || errorMessage;
        } catch (e) {
          // ignore
        }

        this.setState({
          validation: {
            isValid: false,
            errors: [{ code: 'VALIDATION_ERROR', message: errorMessage }]
          }
        });
      }
    } catch (error) {
      console.error('Failed to compile pattern:', error);
      this.setState({
        validation: {
          isValid: false,
          errors: [{ code: 'EXCEPTION', message: translate('NamingBuilderCompileFailed') }]
        }
      });
    }
  };

  handleCancel = () => {
    const { onCancel } = this.props;
    onCancel();
  };

  scheduleValidation = () => {
    if (this.validationTimeout) {
      clearTimeout(this.validationTimeout);
    }

    this.validationTimeout = setTimeout(async () => {
      const clientValidation = validateAst(this.state.ast);

      if (!clientValidation.isValid) {
        this.setState({
          validation: clientValidation,
          isValidating: false
        });
        return;
      }

      this.setState({ isValidating: true });

      try {
        const response = await fetch('/api/v1/config/naming-pattern/validate', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-Api-Key': window.Chaptarr.apiKey
          },
          body: JSON.stringify({ ast: this.state.ast })
        });

        if (response.ok) {
          const serverValidation = await response.json();
          const validation = {
            isValid: Boolean(serverValidation.ok),
            errors: serverValidation.errors || []
          };
          this.setState({ validation, isValidating: false });
          return;
        }

        this.setState({
          validation: {
            isValid: false,
            errors: [{ code: 'VALIDATION_ERROR', message: translate('NamingBuilderValidationRequestFailed') }]
          },
          isValidating: false
        });
      } catch (error) {
        console.error('Validation failed:', error);
        this.setState({
          validation: {
            isValid: false,
            errors: [{ code: 'VALIDATION_ERROR', message: translate('NamingBuilderValidationFailed') }]
          },
          isValidating: false
        });
      }
    }, 300);
  };


  handleDragStart = (event) => {
    const { active } = event;
    
    this.setState({
      draggedItem: {
        id: active.id,
        data: active.data.current
      }
    });
  };

  handleDragEnd = (event) => {
    const { active, over } = event;
    
    this.setState({ draggedItem: null });

    if (!over) return;

    const dragData = active.data.current;
    const dropData = over.data.current;

    if (dragData.source === 'palette') {
      this.handlePaletteDrop(dragData, dropData, over.id);
    } else if (dragData.source === 'canvas') {
      this.handleCanvasMove(active, over);
    }
  };

  handlePaletteDrop = (dragData, dropData, overId) => {
    const { ast } = this.state;
    const newAst = { ...ast };

    // Create new node
    const nodeId = createId();
    let newNode;

    if (dragData.tokenKey === 'Parentheses') {
      // Just treat parentheses as regular separators for now
      newNode = {
        id: nodeId,
        kind: 'separator',
        value: '()'
      };
    } else if (dragData.tokenKey === 'FolderSeparator') {
      newNode = {
        id: nodeId,
        kind: 'separator',
        value: '/'
      };
    } else if (dragData.separator) {
      newNode = {
        id: nodeId,
        kind: 'separator',
        value: dragData.value
      };
    } else {
      newNode = {
        id: nodeId,
        kind: 'token',
        tokenKey: dragData.tokenKey,
        args: dragData.args || {}
      };
    }

    newAst.nodesById[nodeId] = newNode;

    // Insert at position
    if (dropData && dropData.insertIndex !== undefined) {
      newAst.rootIds.splice(dropData.insertIndex, 0, nodeId);
    } else {
      newAst.rootIds.push(nodeId);
    }

    this.updateAst(newAst);
  };

  handleParenthesesWrap = (nodeId) => {
    const { ast } = this.state;
    const newAst = { ...ast };

    // Create group node
    const groupId = createId();
    const groupNode = {
      id: groupId,
      kind: 'group',
      mode: 'paren',
      children: [nodeId],
      omitIfEmpty: true
    };

    newAst.nodesById[groupId] = groupNode;

    // Replace the target node with the group in rootIds
    const nodeIndex = newAst.rootIds.indexOf(nodeId);
    if (nodeIndex >= 0) {
      newAst.rootIds[nodeIndex] = groupId;
    }

    this.updateAst(newAst);
  };

  handleCanvasMove = (active, over) => {
    const { ast } = this.state;
    const newAst = { ...ast, rootIds: [...ast.rootIds] };

    if (!over) return;

    const oldIndex = newAst.rootIds.indexOf(active.id);
    if (oldIndex === -1) return;

    const dropData = over.data.current;
    let newIndex;

    if (over.id.startsWith('dropzone-') && dropData?.insertIndex != null) {
      newIndex = dropData.insertIndex;
    } else {
      newIndex = newAst.rootIds.indexOf(over.id);
    }
    
    if (newIndex === -1) return;

    newAst.rootIds.splice(oldIndex, 1);
    if (newIndex > oldIndex) newIndex--;
    newAst.rootIds.splice(newIndex, 0, active.id);

    this.updateAst(newAst);
  };

  updateAst = (newAst) => {
    this.setState({ ast: newAst });
    this.scheduleValidation();
  };

  handleClear = () => {
    this.updateAst({ nodesById: {}, rootIds: [] });
  };

  handleDeleteNode = (nodeId) => {
    const { ast } = this.state;
    const newAst = { 
      nodesById: { ...ast.nodesById },
      rootIds: [...ast.rootIds]
    };

    // Remove from rootIds
    newAst.rootIds = newAst.rootIds.filter(id => id !== nodeId);
    
    // Remove from nodesById
    delete newAst.nodesById[nodeId];

    // TODO: Handle removal from groups
    
    this.updateAst(newAst);
  };

  render() {
    const { isOpen, isLoading, error } = this.props;
    const { ast, draggedItem, validation, isValidating } = this.state;

    if (!isOpen) {
      return null;
    }

    return (
      <Modal
        isOpen={isOpen}
        onModalClose={this.handleCancel}
        size="extraLarge"
      >
        <ModalContent onModalClose={this.handleCancel}>
          <ModalHeader>
            {translate('NamingBuilderTitle')}
          </ModalHeader>

          <ModalBody>
            {isLoading && <LoadingIndicator />}

            {error && (
              <Alert kind={kinds.DANGER}>
                {translate('NamingBuilderUnableToLoadSettings')}
              </Alert>
            )}

            {!isLoading && !error && (
              <div className={styles.visualBuilder}>
                <DndContext
                  collisionDetection={closestCenter}
                  onDragStart={this.handleDragStart}
                  onDragEnd={this.handleDragEnd}
                >
                  <div className={styles.header}>
                    <h3>{translate('NamingBuilderFilePathHeader')}</h3>
                    <ToolBar 
                      onClear={this.handleClear}
                    />
                  </div>

                  <div className={styles.pathBuilder}>
                    <SortableContext 
                      items={ast.rootIds}
                      strategy={verticalListSortingStrategy}
                    >
                      <NamingCanvas 
                        ast={ast}
                        onDeleteNode={this.handleDeleteNode}
                        isDragging={Boolean(draggedItem)}
                        compact={true}
                      />
                    </SortableContext>
                  </div>

                  <div className={styles.content}>
                    <TokenPalette />
                  </div>

                  <ValidationPanel 
                    validation={validation}
                    isLoading={isValidating}
                  />

                  <DragOverlay>
                    {draggedItem ? (
                      <div className={`${styles.dragOverlay} ${draggedItem.data.color ? styles[draggedItem.data.color] : ''}`}>
                        {draggedItem.data.label}
                      </div>
                    ) : null}
                  </DragOverlay>
                </DndContext>
              </div>
            )}
          </ModalBody>

          <ModalFooter>
            <Button
              onPress={this.handleCancel}
            >
              {translate('Cancel')}
            </Button>

            <Button
              kind={kinds.PRIMARY}
              onPress={this.handleSave}
              isDisabled={!validation.isValid}
            >
              {translate('Save')}
            </Button>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

NamingVisualBuilder.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  initialPattern: PropTypes.string,
  isLoading: PropTypes.bool,
  error: PropTypes.object,
  onSave: PropTypes.func.isRequired,
  onCancel: PropTypes.func.isRequired
};

export default NamingVisualBuilder;
