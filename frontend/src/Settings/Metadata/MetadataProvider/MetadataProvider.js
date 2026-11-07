import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

const writeAudioTagOptions = [
  {
    key: 'no',
    get value() {
      return translate('WriteTagsNo');
    }
  },
  {
    key: 'sync',
    get value() {
      return translate('WriteTagsSync');
    }
  },
  {
    key: 'allFiles',
    get value() {
      return translate('WriteTagsAll');
    }
  },
  {
    key: 'newFiles',
    get value() {
      return translate('WriteTagsNew');
    }
  }
];

const writeBookTagOptions = [
  {
    key: 'sync',
    get value() {
      return translate('WriteTagsSync');
    }
  },
  {
    key: 'allFiles',
    get value() {
      return translate('WriteTagsAll');
    }
  },
  {
    key: 'newFiles',
    get value() {
      return translate('WriteTagsNew');
    }
  }
];

const audibleMarketplaceOptions = [
  ['us', 'AudibleMarketplaceUs'],
  ['uk', 'AudibleMarketplaceUk'],
  ['fr', 'AudibleMarketplaceFr'],
  ['de', 'AudibleMarketplaceDe'],
  ['it', 'AudibleMarketplaceIt'],
  ['es', 'AudibleMarketplaceEs'],
  ['ca', 'AudibleMarketplaceCa'],
  ['au', 'AudibleMarketplaceAu'],
  ['in', 'AudibleMarketplaceIn'],
  ['jp', 'AudibleMarketplaceJp'],
  ['br', 'AudibleMarketplaceBr']
].map(([key, translationKey]) => ({
  key,
  get value() {
    return translate(translationKey);
  }
}));

const metadataCatalogSources = [
  { id: 'metadata-api', name: 'enableMetadataApi', label: 'MetadataApiCatalog', help: 'MetadataApiCatalogSourceHelp', fieldPreference: false },
  { id: 'hardcover', name: 'enableHardcover', label: 'Hardcover', help: 'HardcoverCatalogSourceHelp', fieldPreference: false },
  { id: 'openlibrary', name: 'enableOpenLibrary', label: 'OpenLibrary', help: 'OpenLibrarySourceHelp', fieldPreference: true },
  { id: 'googlebooks', name: 'enableGoogleBooks', label: 'GoogleBooks', help: 'GoogleBooksSourceHelp', fieldPreference: true },
  { id: 'loc', name: 'enableLoc', label: 'LibraryOfCongress', help: 'LocSourceHelp', fieldPreference: true },
  { id: 'gutendex', name: 'enableGutendex', label: 'Gutendex', help: 'GutendexSourceHelp', fieldPreference: true },
  { id: 'internetarchive', name: 'enableInternetArchive', label: 'InternetArchive', help: 'InternetArchiveSourceHelp', fieldPreference: true },
  { id: 'ndl', name: 'enableNdl', label: 'NdlSearch', help: 'NdlSourceHelp', fieldPreference: true },
  { id: 'europeana', name: 'enableEuropeana', label: 'Europeana', help: 'EuropeanaSourceHelp', fieldPreference: true },
  { id: 'apify-goodreads', name: 'enableApifyGoodreads', label: 'ApifyGoodreadsActor', help: 'ApifyGoodreadsSourceHelp', fieldPreference: true }
];

const metadataFieldPreferences = [
  { name: 'metadataTitleSourcePreference', label: 'MetadataTitleSourcePreference' },
  { name: 'metadataDescriptionSourcePreference', label: 'MetadataDescriptionSourcePreference' },
  { name: 'metadataPublisherSourcePreference', label: 'MetadataPublisherSourcePreference' },
  { name: 'metadataLanguageSourcePreference', label: 'MetadataLanguageSourcePreference' },
  { name: 'metadataReleaseDateSourcePreference', label: 'MetadataReleaseDateSourcePreference' },
  { name: 'metadataPageCountSourcePreference', label: 'MetadataPageCountSourcePreference' },
  { name: 'metadataCoverSourcePreference', label: 'MetadataCoverSourcePreference' },
  { name: 'metadataAuthorImageSourcePreference', label: 'MetadataAuthorImageSourcePreference' },
  { name: 'metadataGenresSourcePreference', label: 'MetadataGenresSourcePreference' }
];

const metadataFieldSourceOptions = [
  { key: '', value: translate('UseSelectedMetadataSource') },
  ...metadataCatalogSources.filter((source) => source.fieldPreference).map((source) => ({
    key: source.id,
    get value() {
      return translate(source.label);
    }
  }))
];

function MetadataProvider(props) {
  const {
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  const sourcesFromEnvironment = settings.sourcesFromEnvironment?.value;
  const googleBooksKeyFromEnvironment = settings.googleBooksApiKeyFromEnvironment?.value;
  const europeanaKeyFromEnvironment = settings.europeanaApiKeyFromEnvironment?.value;
  const apifyActorFromEnvironment = settings.apifyGoodreadsActorFromEnvironment?.value;
  const apifyTokenFromEnvironment = settings.apifyTokenFromEnvironment?.value;
  const apifyTemplateFromEnvironment = settings.apifyGoodreadsInputTemplateFromEnvironment?.value;
  const openLibraryEmailFromEnvironment = settings.openLibraryContactEmailFromEnvironment?.value;
  const environmentOverrideHelpText = translate('MetadataProviderCredentialEnvironmentOverride');

  return (

    <div>
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {translate('UnableToLoadMetadataProviderSettings')}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error &&
          <Form>
            <FieldSet legend={translate('AudibleCatalog')}>
              <FormGroup>
                <FormLabel>{translate('AudibleMarketplace')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="audibleMarketplace"
                  helpText={translate('AudibleMarketplaceHelpText')}
                  values={audibleMarketplaceOptions}
                  onChange={onInputChange}
                  {...settings.audibleMarketplace}
                />
              </FormGroup>
            </FieldSet>

            <FieldSet legend={translate('CalibreMetadata')}>
              <FormGroup>
                <FormLabel>
                  {translate('SendMetadataToCalibre')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="writeBookTags"
                  helpTextWarning={translate('WriteBookTagsHelpTextWarning')}
                  helpLink="https://wiki.servarr.com/readarr/settings#write-metadata-to-book-files"
                  values={writeBookTagOptions}
                  onChange={onInputChange}
                  {...settings.writeBookTags}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('UpdateCovers')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="updateCovers"
                  helpText={translate('UpdateCoversHelpText')}
                  onChange={onInputChange}
                  {...settings.updateCovers}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>
                  {translate('EmbedMetadataInBookFiles')}
                </FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="embedMetadata"
                  helpText={translate('EmbedMetadataHelpText')}
                  onChange={onInputChange}
                  {...settings.embedMetadata}
                />
              </FormGroup>

            </FieldSet>

            <FieldSet legend={translate('RuntimeCatalogSources')}>
              <p className="helpText">
                {sourcesFromEnvironment ?
                  translate('AdditionalMetadataSourcesEnvironmentOverride') :
                  translate('RuntimeCatalogSourcesHelpText')}
              </p>

              {metadataCatalogSources.map((source) => (
                <FormGroup key={source.name}>
                  <FormLabel>{translate(source.label)}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name={source.name}
                    helpText={translate(source.help)}
                    onChange={onInputChange}
                    {...settings[source.name]}
                    isDisabled={sourcesFromEnvironment}
                  />
                </FormGroup>
              ))}

              <FormGroup>
                <FormLabel>{translate('OpenLibraryContactEmail')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="openLibraryContactEmail"
                  onChange={onInputChange}
                  {...settings.openLibraryContactEmail}
                  helpTexts={openLibraryEmailFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={openLibraryEmailFromEnvironment}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('GoogleBooksApiKey')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="googleBooksApiKey"
                  placeholder={translate('EnterToReplaceSavedKey')}
                  onChange={onInputChange}
                  {...settings.googleBooksApiKey}
                  helpTexts={googleBooksKeyFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={googleBooksKeyFromEnvironment}
                  autoComplete="new-password"
                />
                {settings.hasGoogleBooksApiKey?.value && (
                  <p className="helpText">{translate('MetadataProviderKeySaved')}</p>
                )}
                {settings.hasGoogleBooksApiKey?.value && !googleBooksKeyFromEnvironment && (
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="clearGoogleBooksApiKey"
                    helpText={translate('ClearSavedMetadataKeyHelpText')}
                    onChange={onInputChange}
                    {...settings.clearGoogleBooksApiKey}
                  />
                )}
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('EuropeanaApiKey')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="europeanaApiKey"
                  placeholder={translate('EnterToReplaceSavedKey')}
                  onChange={onInputChange}
                  {...settings.europeanaApiKey}
                  helpTexts={europeanaKeyFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={europeanaKeyFromEnvironment}
                  autoComplete="new-password"
                />
                {settings.hasEuropeanaApiKey?.value && (
                  <p className="helpText">{translate('MetadataProviderKeySaved')}</p>
                )}
                {settings.hasEuropeanaApiKey?.value && !europeanaKeyFromEnvironment && (
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="clearEuropeanaApiKey"
                    helpText={translate('ClearSavedMetadataKeyHelpText')}
                    onChange={onInputChange}
                    {...settings.clearEuropeanaApiKey}
                  />
                )}
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('ApifyGoodreadsActor')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT}
                  name="apifyGoodreadsActor"
                  placeholder="publisher~actor-name"
                  onChange={onInputChange}
                  {...settings.apifyGoodreadsActor}
                  helpTexts={apifyActorFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={apifyActorFromEnvironment}
                />
                {settings.apifyGoodreadsActor?.value && !apifyActorFromEnvironment && (
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="clearApifyGoodreadsActor"
                    helpText={translate('ClearApifyGoodreadsActorHelpText')}
                    onChange={onInputChange}
                    {...settings.clearApifyGoodreadsActor}
                  />
                )}
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('ApifyToken')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.PASSWORD}
                  name="apifyToken"
                  placeholder={translate('EnterToReplaceSavedKey')}
                  onChange={onInputChange}
                  {...settings.apifyToken}
                  helpTexts={apifyTokenFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={apifyTokenFromEnvironment}
                  autoComplete="new-password"
                />
                {settings.hasApifyToken?.value && (
                  <p className="helpText">{translate('MetadataProviderKeySaved')}</p>
                )}
                {settings.hasApifyToken?.value && !apifyTokenFromEnvironment && (
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="clearApifyToken"
                    helpText={translate('ClearSavedMetadataKeyHelpText')}
                    onChange={onInputChange}
                    {...settings.clearApifyToken}
                  />
                )}
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('ApifyGoodreadsInputTemplate')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.TEXT_AREA}
                  name="apifyGoodreadsInputTemplate"
                  placeholder={'{"searchQueries":[{{query}}],"maxItems":10}'}
                  helpText={translate('ApifyGoodreadsInputTemplateHelpText')}
                  onChange={onInputChange}
                  {...settings.apifyGoodreadsInputTemplate}
                  helpTexts={apifyTemplateFromEnvironment ? [environmentOverrideHelpText] : []}
                  isDisabled={apifyTemplateFromEnvironment}
                />
              </FormGroup>
            </FieldSet>

            <FieldSet legend={translate('MetadataFieldSourcePreferences')}>
              <p className="helpText">
                {translate('MetadataFieldSourcePreferencesHelpText')}
              </p>
              {metadataFieldPreferences.map((field) => (
                <FormGroup key={field.name}>
                  <FormLabel>{translate(field.label)}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name={field.name}
                    values={metadataFieldSourceOptions}
                    onChange={onInputChange}
                    {...settings[field.name]}
                  />
                </FormGroup>
              ))}
            </FieldSet>

            <FieldSet legend={translate('AudioFileMetadata')}>
              <FormGroup>
                <FormLabel>{translate('WriteAudioTags')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="writeAudioTags"
                  helpTextWarning={translate('WriteBookTagsHelpTextWarning')}
                  helpLink="https://wiki.servarr.com/readarr/settings#write-metadata-to-audio-files"
                  values={writeAudioTagOptions}
                  onChange={onInputChange}
                  {...settings.writeAudioTags}
                />
              </FormGroup>

              <FormGroup>
                <FormLabel>{translate('WriteAudioTagsScrub')}</FormLabel>

                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="scrubAudioTags"
                  helpTextWarning={translate('WriteAudioTagsScrubHelp')}
                  onChange={onInputChange}
                  {...settings.scrubAudioTags}
                />
              </FormGroup>

            </FieldSet>
          </Form>
      }
    </div>

  );
}

MetadataProvider.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default MetadataProvider;
