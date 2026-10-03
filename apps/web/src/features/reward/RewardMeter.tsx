import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { cn } from '@/lib/utils';
import { chickenOffset, EGG_COUNT } from './rewardModel.ts';

/** Horizontal room of the chicken in the picture: it walks 1.9 units for every percent. */
const TRACK_UNITS_PER_PERCENT = 1.9;
/** How long the chicken keeps its walking bounce after a change, in milliseconds. */
const WALK_MS = 1700;
/** Eggs per row in the basket; the basket holds two rows. */
const EGGS_PER_ROW = 5;

/** The middle of egg number `index` (0-based) in the basket, in picture units. */
function eggCentre(index: number): { x: number; y: number } {
  const row = Math.floor(index / EGGS_PER_ROW);
  const column = index % EGGS_PER_ROW;
  return { x: 280 + column * 15 + (row === 1 ? 7 : 0), y: row === 0 ? 100 : 86 };
}

/**
 * The picture of the reward meter (requirements 4.12): a chicken that walks along the track to the current percentage, and a
 * basket that holds one egg per full 10%. It is one inline image with a text alternative; the progress itself is also
 * written out as text and as a progress bar next to it, so nothing depends on the picture. The chicken moves with a CSS
 * transform; with reduced motion it stands in place at once and nothing animates. `celebrating` plays the completion
 * animation once: the eggs bounce into the basket.
 */
export function RewardMeter({
  percent,
  eggs,
  label,
  celebrating,
  reducedMotion,
  onCelebrationEnd,
}: {
  percent: number;
  eggs: number;
  label: string;
  celebrating: boolean;
  reducedMotion: boolean;
  /** Called when the last egg has landed, so the page can stop treating the animation as running. */
  onCelebrationEnd?: () => void;
}) {
  const target = chickenOffset(percent);
  // The chicken starts at the beginning of the track and walks to its place, unless motion is not wanted.
  const [position, setPosition] = useState(reducedMotion ? target : 0);
  const [walking, setWalking] = useState(false);

  useEffect(() => {
    if (reducedMotion) {
      setPosition(target);
      setWalking(false);
      return;
    }
    const start = setTimeout(() => {
      setPosition(target);
      setWalking(true);
    }, 60);
    const stop = setTimeout(() => setWalking(false), 60 + WALK_MS);
    return () => {
      clearTimeout(start);
      clearTimeout(stop);
    };
  }, [target, reducedMotion]);

  // A native listener rather than `onAnimationEnd`, so the end is also seen where React has no animation events (tests).
  const lastEgg = useRef<SVGGElement>(null);
  useEffect(() => {
    const egg = lastEgg.current;
    if (!celebrating || !egg || !onCelebrationEnd) return;
    egg.addEventListener('animationend', onCelebrationEnd);
    return () => egg.removeEventListener('animationend', onCelebrationEnd);
  }, [celebrating, onCelebrationEnd]);

  return (
    <svg
      role="img"
      aria-label={label}
      viewBox="0 0 360 150"
      className={cn('reward-scene h-auto w-full max-w-xl', walking && 'reward-walking', celebrating && 'reward-celebrating')}
      data-celebrating={celebrating ? 'true' : 'false'}
    >
      <g aria-hidden="true">
        {/* The ground the chicken walks on. */}
        <line x1="6" y1="120" x2="250" y2="120" stroke="var(--border)" strokeWidth="4" strokeLinecap="round" />
        <path d="M18 128h10M60 130h14M120 128h8M190 130h12M232 128h8" stroke="var(--border)" strokeWidth="2" strokeLinecap="round" />

        {/* Empty places in the basket are dashed outlines, so a missing egg is never only a missing colour. */}
        {Array.from({ length: EGG_COUNT }, (_, index) => {
          const { x, y } = eggCentre(index);
          return index < eggs ? null : (
            <ellipse key={`slot-${index}`} cx={x} cy={y} rx="6" ry="7.5" fill="none" stroke="var(--muted-foreground)" strokeWidth="1.5" strokeDasharray="3 2.5" />
          );
        })}
        {Array.from({ length: Math.min(EGG_COUNT, eggs) }, (_, index) => {
          const { x, y } = eggCentre(index);
          return (
            <g
              key={`egg-${index}`}
              className="reward-egg"
              style={{ '--egg-index': index } as CSSProperties}
              ref={index === EGG_COUNT - 1 ? lastEgg : undefined}
            >
              <ellipse cx={x} cy={y} rx="6.5" ry="8" fill="oklch(0.97 0.03 85)" stroke="var(--foreground)" strokeWidth="1.5" />
              <path d={`M${x - 3} ${y - 2}q2-3 4-2`} fill="none" stroke="var(--primary)" strokeWidth="1.5" strokeLinecap="round" />
            </g>
          );
        })}

        {/* The basket in front of the eggs. */}
        <path d="M268 108h94l-9 24q-1.5 4-6 4h-64q-4.5 0-6-4z" fill="color-mix(in oklch, var(--primary) 35%, var(--card))" stroke="var(--foreground)" strokeWidth="2" strokeLinejoin="round" />
        <path d="M274 118h82M277 126h76" stroke="var(--foreground)" strokeWidth="1.2" opacity="0.5" />
        <path d="M262 108h106" stroke="var(--foreground)" strokeWidth="3.5" strokeLinecap="round" />

        {/* The chicken, facing the basket; it walks with a CSS transform. */}
        <g
          className="reward-chicken"
          data-testid="reward-chicken"
          data-position={position}
          style={{ transform: `translateX(${position * TRACK_UNITS_PER_PERCENT}px)` }}
        >
          <g className="reward-chicken-body">
            <path d="M12 86q-12-6-8-20q8 6 14 12z" fill="var(--card)" stroke="var(--foreground)" strokeWidth="2" strokeLinejoin="round" />
            <ellipse cx="28" cy="92" rx="20" ry="16" fill="var(--card)" stroke="var(--foreground)" strokeWidth="2" />
            <path d="M18 94q10 12 22 0" fill="none" stroke="var(--foreground)" strokeWidth="1.5" strokeLinecap="round" opacity="0.6" />
            <circle cx="46" cy="74" r="10" fill="var(--card)" stroke="var(--foreground)" strokeWidth="2" />
            <path d="M41 66q1-9 5-3q3-7 6 2" fill="var(--destructive)" stroke="var(--foreground)" strokeWidth="1.5" strokeLinejoin="round" />
            <path d="M55 72l9 4l-9 4z" fill="var(--warning)" stroke="var(--foreground)" strokeWidth="1.5" strokeLinejoin="round" />
            <path d="M52 82q1 6 4 3" fill="var(--destructive)" stroke="var(--foreground)" strokeWidth="1.2" />
            <circle cx="49" cy="72" r="1.8" fill="var(--foreground)" />
          </g>
          <path d="M24 106v14m-4 0h8M36 106v14m-4 0h8" stroke="var(--foreground)" strokeWidth="2.5" strokeLinecap="round" />
        </g>
      </g>
    </svg>
  );
}
