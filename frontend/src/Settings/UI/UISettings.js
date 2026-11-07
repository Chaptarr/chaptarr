import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { inputTypes, kinds } from 'Helpers/Props';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import themes from 'Styles/Themes';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import {
  applyDocumentLocale,
  getLocale,
  getUiLocaleById,
  getUiLocaleOptionLabel,
  SUPPORTED_UI_LOCALE_IDS,
  SUPPORTED_UI_LOCALES
} from 'Utilities/Localization/locale';
import titleCase from 'Utilities/String/titleCase';
import translate, { translateWithFallback } from 'Utilities/String/translate';
import styles from './UISettings.css';

const { formatDate, formatDateParts } = intlFormatters;

function getDateParts(options) {
  const date = new Date(Date.UTC(2014, 2, 25, 17, 0));

  return formatDateParts(date, {
    timeZone: 'UTC',
    ...options
  }, getLocale());
}

function getTime(hour, minute, hour12) {
  const date = new Date(Date.UTC(2014, 2, 25, hour, minute));
  return formatDate(date, {
    timeZone: 'UTC',
    hour: 'numeric',
    minute: minute ? '2-digit' : undefined,
    hour12
  }, getLocale());
}

export function getFirstDayOfWeekOptions() {
  return [
    { key: 0, value: translate('Sunday') },
    { key: 1, value: translate('Monday') }
  ];
}

export function getWeekColumnOptions() {
  const { weekday, month, day } = getDateParts({
    weekday: 'short',
    month: 'numeric',
    day: 'numeric'
  });
  const { month: paddedMonth, day: paddedDay } = getDateParts({
    month: '2-digit',
    day: '2-digit'
  });

  return [
    { key: 'ddd M/D', value: `${weekday} ${month}/${day}`, hint: 'ddd M/D' },
    {
      key: 'ddd MM/DD',
      value: `${weekday} ${paddedMonth}/${paddedDay}`,
      hint: 'ddd MM/DD'
    },
    { key: 'ddd D/M', value: `${weekday} ${day}/${month}`, hint: 'ddd D/M' },
    {
      key: 'ddd DD/MM',
      value: `${weekday} ${paddedDay}/${paddedMonth}`,
      hint: 'ddd DD/MM'
    }
  ];
}

function getShortDateFormatOptions() {
  const monthShort = getDateParts({ month: 'short' }).month;
  const numeric = getDateParts({
    month: '2-digit',
    day: '2-digit',
    year: 'numeric'
  });

  return [
    {
      key: 'MMM D YYYY',
      value: `${monthShort} ${numeric.day} ${numeric.year}`,
      hint: 'MMM D YYYY'
    },
    {
      key: 'DD MMM YYYY',
      value: `${numeric.day} ${monthShort} ${numeric.year}`,
      hint: 'DD MMM YYYY'
    },
    {
      key: 'MM/D/YYYY',
      value: `${numeric.month}/${numeric.day}/${numeric.year}`,
      hint: 'MM/D/YYYY'
    },
    {
      key: 'MM/DD/YYYY',
      value: `${numeric.month}/${numeric.day}/${numeric.year}`,
      hint: 'MM/DD/YYYY'
    },
    {
      key: 'DD/MM/YYYY',
      value: `${numeric.day}/${numeric.month}/${numeric.year}`,
      hint: 'DD/MM/YYYY'
    },
    {
      key: 'YYYY-MM-DD',
      value: `${numeric.year}-${numeric.month}-${numeric.day}`,
      hint: 'YYYY-MM-DD'
    }
  ];
}

function getLongDateFormatOptions() {
  const first = getDateParts({
    weekday: 'long',
    month: 'long',
    day: 'numeric',
    year: 'numeric'
  });
  const second = getDateParts({
    month: 'long',
    day: 'numeric',
    year: 'numeric'
  });

  return [
    {
      key: 'dddd, MMMM D YYYY',
      value: `${first.weekday}, ${first.month} ${first.day}, ${first.year}`
    },
    {
      key: 'dddd, D MMMM YYYY',
      value: `${first.weekday}, ${second.day} ${second.month}, ${second.year}`
    }
  ];
}

export function getTimeFormatOptions() {
  return [
    {
      key: 'h(:mm)a',
      value: `${getTime(17, 0, true)}/${getTime(17, 30, true)}`,
      hint: 'h(:mm)a'
    },
    {
      key: 'HH:mm',
      value: `${getTime(17, 0, false)}/${getTime(17, 30, false)}`,
      hint: 'HH:mm'
    }
  ];
}

class UISettings extends Component {
  componentDidMount() {
    const uiLanguage = this.props.settings && this.props.settings.uiLanguage;
    const selectedLocale = uiLanguage && getUiLocaleById(uiLanguage.value);
    if (selectedLocale) {
      applyDocumentLocale(selectedLocale.tag);
    }
  }

  componentDidUpdate(previousProps) {
    const previousLanguage = previousProps.settings.uiLanguage &&
      previousProps.settings.uiLanguage.value;
    const currentLanguage = this.props.settings.uiLanguage &&
      this.props.settings.uiLanguage.value;

    if (currentLanguage !== previousLanguage) {
      const selectedLocale = getUiLocaleById(currentLanguage);
      if (selectedLocale) {
        applyDocumentLocale(selectedLocale.tag);
      }
    }
  }

  //
  // Render

  render() {
    const {
      isFetching,
      error,
      settings,
      hasSettings,
      onInputChange,
      onSavePress,
      languages,
      ...otherProps
    } = this.props;

    const uiLanguages = SUPPORTED_UI_LOCALES.map((locale) => ({
      key: locale.id,
      value: getUiLocaleOptionLabel(locale)
    }));
    const currentUiLanguageId = Number(settings.uiLanguage && settings.uiLanguage.value);
    const currentLegacyLanguage = languages.find((language) =>
      language.key === currentUiLanguageId && language.value !== 'Original'
    );

    if (!SUPPORTED_UI_LOCALE_IDS.has(currentUiLanguageId) && currentLegacyLanguage) {
      const currentLegacyLocale = getUiLocaleById(currentUiLanguageId);
      uiLanguages.push({
        ...currentLegacyLanguage,
        value: currentLegacyLocale ?
          getUiLocaleOptionLabel(currentLegacyLocale) :
          currentLegacyLanguage.value
      });
    }
    const defaultMediaTypeSetting = settings.addNewDefaultMediaType || {
      value: 'audiobook'
    };
    const themeOptions = Object.keys(themes).map((theme) => ({
      key: theme,
      value:
        theme === 'auto' ? translate('Automatic') : translate(titleCase(theme))
    }));
    const mediaTypeOptions = [
      { key: 'audiobook', value: translate('AudiobookLabel') },
      { key: 'ebook', value: translate('EbookLabel') },
      { key: 'both', value: translate('AudiobooksAndEbooks') }
    ];
    const experienceModeOptions = [
      {
        key: 'simple',
        label: translate('SimpleExperience'),
        description: translate('SimpleExperienceDescription')
      },
      {
        key: 'advanced',
        label: translate('AdvancedExperience'),
        description: translate('AdvancedExperienceDescription')
      }
    ];
    const experienceModeSetting = settings.experienceMode || {};
    const experienceMode = experienceModeSetting.value || 'advanced';

    return (
      <PageContent title={translate('UISettings')}>
        <SettingsToolbarConnector {...otherProps} onSavePress={onSavePress} />

        <PageContentBody>
          {isFetching && <LoadingIndicator />}

          {!isFetching && error && (
            <Alert kind={kinds.DANGER}>
              {translate('UnableToLoadUISettings')}
            </Alert>
          )}

          {hasSettings && !isFetching && !error && (
            <Form id="uiSettings" {...otherProps}>
              <FieldSet legend={translate('ExperienceMode')}>
                <FormGroup>
                  <p id="experience-mode-help" className={styles.experienceModeHelpText}>
                    {translate('ExperienceModeHelpText')}
                  </p>

                  <div className={styles.experienceModeOptions}>
                    {experienceModeOptions.map((option) => {
                      const labelId = `experience-mode-${option.key}-label`;
                      const descriptionId = `experience-mode-${option.key}-description`;

                      return (
                        <label
                          key={option.key}
                          className={classNames(
                            styles.experienceModeOption,
                            experienceMode === option.key && styles.isSelected
                          )}
                        >
                          <input
                            id={`experience-mode-${option.key}`}
                            className={styles.experienceModeRadio}
                            type="radio"
                            name="experienceMode"
                            value={option.key}
                            checked={experienceMode === option.key}
                            aria-labelledby={labelId}
                            aria-describedby={`experience-mode-help ${descriptionId}`}
                            onChange={() => {
                              onInputChange({ name: 'experienceMode', value: option.key });
                            }}
                          />

                          <span className={styles.experienceModeText}>
                            <span id={labelId} className={styles.experienceModeTitle}>
                              {option.label}
                            </span>

                            <span id={descriptionId} className={styles.experienceModeDescription}>
                              {option.description}
                            </span>
                          </span>
                        </label>
                      );
                    })}
                  </div>
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('AddNewItem')}>
                <FormGroup>
                  <FormLabel name="addNewDefaultMediaType">{translate('DefaultFormatForNewItems')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="addNewDefaultMediaType"
                    values={mediaTypeOptions}
                    helpText={translate('DefaultFormatForNewItemsHelpText')}
                    onChange={onInputChange}
                    {...defaultMediaTypeSetting}
                    value={defaultMediaTypeSetting.value || 'audiobook'}
                  />
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('Calendar')}>
                <FormGroup>
                  <FormLabel name="firstDayOfWeek">{translate('FirstDayOfWeek')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="firstDayOfWeek"
                    values={getFirstDayOfWeekOptions()}
                    onChange={onInputChange}
                    {...settings.firstDayOfWeek}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel name="calendarWeekColumnHeader">{translate('WeekColumnHeader')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="calendarWeekColumnHeader"
                    values={getWeekColumnOptions()}
                    onChange={onInputChange}
                    helpText={translate('CalendarWeekColumnHeaderHelpText')}
                    {...settings.calendarWeekColumnHeader}
                  />
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('Dates')}>
                <FormGroup>
                  <FormLabel name="shortDateFormat">{translate('ShortDateFormat')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="shortDateFormat"
                    values={getShortDateFormatOptions()}
                    onChange={onInputChange}
                    {...settings.shortDateFormat}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel name="longDateFormat">{translate('LongDateFormat')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="longDateFormat"
                    values={getLongDateFormatOptions()}
                    onChange={onInputChange}
                    {...settings.longDateFormat}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel name="timeFormat">{translate('TimeFormat')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="timeFormat"
                    values={getTimeFormatOptions()}
                    onChange={onInputChange}
                    {...settings.timeFormat}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel name="showRelativeDates">{translate('ShowRelativeDates')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="showRelativeDates"
                    helpText={translate('ShowRelativeDatesHelpText')}
                    onChange={onInputChange}
                    {...settings.showRelativeDates}
                  />
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('Style')}>
                <FormGroup>
                  <FormLabel name="theme">{translate('Theme')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="theme"
                    helpText={translate('ThemeHelpText')}
                    values={themeOptions}
                    onChange={onInputChange}
                    {...settings.theme}
                  />
                </FormGroup>

                <FormGroup>
                  <FormLabel name="enableColorImpairedMode">{translate('EnableColorImpairedMode')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="enableColorImpairedMode"
                    helpText={translate('EnableColorImpairedModeHelpText')}
                    onChange={onInputChange}
                    {...settings.enableColorImpairedMode}
                  />
                </FormGroup>
              </FieldSet>

              <FieldSet legend={translate('Language')}>
                <FormGroup>
                  <FormLabel name="uiLanguage">{translate('UILanguage')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="uiLanguage"
                    values={uiLanguages}
                    helpText={`${translate('UILanguageHelpText')} ${translateWithFallback(
                      'ui.language.inheritedFallbackNotice',
                      'Untranslated labels use English until a translation is added.'
                    )}`}
                    helpTextWarning={translate('UILanguageHelpTextWarning')}
                    onChange={onInputChange}
                    {...settings.uiLanguage}
                    errors={
                      SUPPORTED_UI_LOCALE_IDS.has(Number(settings.uiLanguage.value)) ||
                      !!currentLegacyLanguage ?
                        settings.uiLanguage.errors :
                        [
                          ...settings.uiLanguage.errors,
                          { message: translate('InvalidUILanguage') }
                        ]
                    }
                  />
                </FormGroup>
              </FieldSet>
            </Form>
          )}
        </PageContentBody>
      </PageContent>
    );
  }
}

UISettings.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onSavePress: PropTypes.func.isRequired,
  languages: PropTypes.arrayOf(PropTypes.object).isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default UISettings;
