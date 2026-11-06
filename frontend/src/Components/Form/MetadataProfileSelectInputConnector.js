import React from 'react';
import ProfileSelectInputConnector from './ProfileSelectInputConnector';

export default function MetadataProfileSelectInputConnector(props) {
  return <ProfileSelectInputConnector {...props} profileKind="metadata" />;
}
