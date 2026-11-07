import translate from 'Utilities/String/translate';

// This is the output of eg. http://192.168.0.3:8089/conversion/book-data/100

const profileData = {
  cybookg3: {
    name: 'Cybook G3',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Cybook G3',
    screenSize: '600 x 800'
  },
  cybook_opus: {
    name: 'Cybook Opus',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Cybook Opus',
    screenSize: '590 x 775'
  },
  default: {
    nameKey: 'DefaultOutputProfile',
    descriptionKey: 'CalibreProfileGeneralPurpose',
    screenSize: '1600 x 1200'
  },
  generic_eink: {
    nameKey: 'GenericEinkProfile',
    descriptionKey: 'CalibreProfileSuitableForEink',
    screenSize: '590 x 775'
  },
  generic_eink_hd: {
    nameKey: 'GenericEinkHDProfile',
    descriptionKey: 'CalibreProfileSuitableForModernEink',
    screenSize: 'unlimited'
  },
  generic_eink_large: {
    nameKey: 'GenericLargeEinkProfile',
    descriptionKey: 'CalibreProfileSuitableForLargeEink',
    screenSize: '600 x 999'
  },
  hanlinv3: {
    name: 'Hanlin V3',
    descriptionKey: 'CalibreProfileIntendedForDeviceAndClones',
    device: 'Hanlin V3',
    screenSize: '584 x 754'
  },
  hanlinv5: {
    name: 'Hanlin V5',
    descriptionKey: 'CalibreProfileIntendedForDeviceAndClones',
    device: 'Hanlin V5',
    screenSize: '584 x 754'
  },
  illiad: {
    name: 'Illiad',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Irex Illiad',
    screenSize: '760 x 925'
  },
  ipad: {
    name: 'iPad',
    descriptionKey: 'CalibreProfileIntendedForIpadResolution',
    resolution: '768x1024',
    screenSize: '768 x 1024'
  },
  ipad3: {
    name: 'iPad 3',
    descriptionKey: 'CalibreProfileIntendedForIpadResolution',
    resolution: '1536x2048',
    screenSize: '2048 x 1536'
  },
  irexdr1000: {
    name: 'IRex Digital Reader 1000',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'IRex Digital Reader 1000',
    screenSize: '1024 x 1280'
  },
  irexdr800: {
    name: 'IRex Digital Reader 800',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'IRex Digital Reader 800',
    screenSize: '768 x 1024'
  },
  jetbook5: {
    name: 'JetBook 5-inch',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: '5-inch JetBook',
    screenSize: '480 x 640'
  },
  kindle: {
    name: 'Kindle',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle',
    screenSize: '525 x 640'
  },
  kindle_dx: {
    name: 'Kindle DX',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle DX',
    screenSize: '744 x 1022'
  },
  kindle_fire: {
    name: 'Kindle Fire',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle Fire',
    screenSize: '570 x 1016'
  },
  kindle_oasis: {
    name: 'Kindle Oasis',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle Oasis 2017 and above',
    screenSize: '1264 x 1680'
  },
  kindle_pw: {
    name: 'Kindle PaperWhite',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle PaperWhite 1 and 2',
    screenSize: '658 x 940'
  },
  kindle_pw3: {
    name: 'Kindle PaperWhite 3',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle PaperWhite 3 and above',
    screenSize: '1072 x 1430'
  },
  kindle_voyage: {
    name: 'Kindle Voyage',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Amazon Kindle Voyage',
    screenSize: '1080 x 1430'
  },
  kobo: {
    name: 'Kobo Reader',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Kobo Reader',
    screenSize: '536 x 710'
  },
  msreader: {
    name: 'Microsoft Reader',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Microsoft Reader',
    screenSize: '480 x 652'
  },
  mobipocket: {
    nameKey: 'MobipocketBooksProfile',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'Mobipocket books',
    screenSize: '600 x 800'
  },
  nook: {
    name: 'Nook',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'B&N Nook',
    screenSize: '600 x 730'
  },
  nook_color: {
    name: 'Nook Color',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'B&N Nook Color',
    screenSize: '600 x 900'
  },
  nook_hd_plus: {
    name: 'Nook HD+',
    descriptionKey: 'CalibreProfileIntendedForDeviceResolution',
    device: 'Nook HD+',
    resolution: '1280x1920',
    screenSize: '1280 x 1920'
  },
  pocketbook_900: {
    name: 'PocketBook Pro 900',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'PocketBook Pro 900 series of devices',
    screenSize: '810 x 1180'
  },
  pocketbook_pro_912: {
    name: 'PocketBook Pro 912',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'PocketBook Pro 912 series of devices',
    screenSize: '825 x 1200'
  },
  galaxy: {
    name: 'Samsung Galaxy',
    descriptionKey: 'CalibreProfileIntendedForDeviceResolution',
    device: 'Samsung Galaxy',
    resolution: '600x1280',
    screenSize: '600 x 1280'
  },
  sony: {
    name: 'Sony Reader',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'SONY PRS line (500/505/600/700, etc.)',
    screenSize: '590 x 775'
  },
  sony300: {
    name: 'Sony Reader 300',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'SONY PRS-300',
    screenSize: '590 x 775'
  },
  sony900: {
    name: 'Sony Reader 900',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'SONY PRS-900',
    screenSize: '600 x 999'
  },
  sony_landscape: {
    name: 'Sony Reader Landscape',
    descriptionKey: 'CalibreProfileDeviceLandscape',
    device: 'SONY PRS line (500/505/700, etc.)',
    screenSize: '784 x 1012'
  },
  sonyt3: {
    name: 'Sony Reader T3',
    descriptionKey: 'CalibreProfileIntendedForDevice',
    device: 'SONY PRS-T3',
    screenSize: '758 x 934'
  },
  tablet: {
    nameKey: 'Tablet',
    descriptionKey: 'CalibreProfileGenericTabletNoResize',
    screenSize: 'unlimited'
  }
};

export function getOptions() {
  return Object.entries(profileData).map(([key, profile]) => {
    const tokens = {
      device: profile.device,
      resolution: profile.resolution,
      screenSize: profile.screenSize
    };

    return {
      key,
      value: profile.nameKey ? translate(profile.nameKey) : profile.name,
      description: translate(profile.descriptionKey, tokens)
    };
  });
}
