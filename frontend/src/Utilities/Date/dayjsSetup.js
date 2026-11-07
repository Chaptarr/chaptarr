import dayjs from 'dayjs';
import customParseFormat from 'dayjs/plugin/customParseFormat';
import duration from 'dayjs/plugin/duration';
import isBetween from 'dayjs/plugin/isBetween';
import isoWeek from 'dayjs/plugin/isoWeek';
import relativeTime from 'dayjs/plugin/relativeTime';
import weekOfYear from 'dayjs/plugin/weekOfYear';

dayjs.extend(relativeTime);
dayjs.extend(duration);
dayjs.extend(weekOfYear);
dayjs.extend(isoWeek);
dayjs.extend(customParseFormat);
dayjs.extend(isBetween);

const dayjsSetup = dayjs;

export default dayjsSetup;
