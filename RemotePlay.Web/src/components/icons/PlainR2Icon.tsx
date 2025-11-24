import React, { useMemo } from 'react'

interface PlainR2IconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function PlainR2Icon({ className = '', style = {}, size = 46 }: PlainR2IconProps) {
  const filter0 = useMemo(() => `f0-${Math.random().toString(36).slice(2)}`, [])
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 46 46"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      style={{
        ...style,
        shapeRendering: 'auto',
        textRendering: 'optimizeLegibility',
        imageRendering: 'auto',
      }}
      preserveAspectRatio="xMidYMid meet"
    >
      <defs>
        {/* drop shadow */}
        <filter
          id={filter0}
          x="1.24264"
          y="6.62742"
          width="37.3848"
          height="37.3848"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feComposite in2="hardAlpha" operator="out" />
          <feColorMatrix type="matrix" values="0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 0.2 0" />
          <feBlend mode="normal" in2="BackgroundImageFix" result="effect1_dropShadow" />
          <feBlend mode="normal" in="SourceGraphic" in2="effect1_dropShadow" result="shape" />
        </filter>

        {/* inner glow */}
        <filter
          id={filter1}
          x="1.24264"
          y="6.62742"
          width="37.3848"
          height="37.3848"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feBlend mode="normal" in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feGaussianBlur stdDeviation="2.5" />
          <feComposite in2="hardAlpha" operator="arithmetic" k2="-1" k3="1" />
          <feColorMatrix
            type="matrix"
            values="0 0 0 0 0.985577 0 0 0 0 0.991048 0 0 0 0 1 0 0 0 1 0"
          />
          <feBlend mode="normal" in2="shape" result="effect1_innerShadow" />
        </filter>
      </defs>

      <g filter={`url(#${filter0})`}>
        <g filter={`url(#${filter1})`}>
          <path
            d="M11.3137 11.3137C17.5621 5.06532 27.6927 5.06532 33.9411 11.3137C40.1895 17.5621 40.1895 27.6927 33.9411 33.9411L24.7487 43.1335C23.5772 44.3051 21.6777 44.3051 20.5061 43.1335L2.12132 24.7487C0.949747 23.5772 0.949747 21.6777 2.12132 20.5061L11.3137 11.3137Z"
            fill="#7F8A9C"
            fillOpacity="0.01"
          />
        </g>

        <path
          d="M16.1691 27.6279V18.9006H19.6123C20.2714 18.9006 20.8339 19.0185 21.2998 19.2543C21.7686 19.4873 22.1251 19.8182 22.3694 20.2472C22.6166 20.6734 22.7402 21.1748 22.7402 21.7515C22.7402 22.331 22.6152 22.8296 22.3652 23.2472C22.1152 23.662 21.7529 23.9802 21.2785 24.2018C20.8069 24.4234 20.2359 24.5342 19.5654 24.5342H17.26V23.0512H19.2671C19.6194 23.0512 19.912 23.0029 20.145 22.9063C20.3779 22.8097 20.5512 22.6648 20.6649 22.4717C20.7814 22.2785 20.8396 22.0384 20.8396 21.7515C20.8396 21.4617 20.7814 21.2174 20.6649 21.0185C20.5512 20.8197 20.3765 20.6691 20.1407 20.5668C19.9078 20.4617 19.6137 20.4092 19.2586 20.4092H18.0143V27.6279H16.1691ZM20.8822 23.6563L23.0512 27.6279H21.0143L18.8921 23.6563H20.8822ZM23.993 27.6279V26.2984L27.0995 23.4219C27.3637 23.1663 27.5853 22.9361 27.7643 22.7316C27.9461 22.5271 28.0839 22.3268 28.1777 22.1307C28.2714 21.9319 28.3183 21.7174 28.3183 21.4873C28.3183 21.2316 28.26 21.0114 28.1436 20.8268C28.0271 20.6393 27.868 20.4958 27.6663 20.3964C27.4646 20.2941 27.2359 20.243 26.9802 20.243C26.7132 20.243 26.4802 20.2969 26.2814 20.4049C26.0825 20.5129 25.9291 20.6677 25.8211 20.8694C25.7132 21.0711 25.6592 21.3111 25.6592 21.5896H23.9078C23.9078 21.0185 24.037 20.5228 24.2956 20.1023C24.5541 19.6819 24.9163 19.3566 25.3822 19.1265C25.8481 18.8964 26.385 18.7813 26.993 18.7813C27.618 18.7813 28.162 18.8921 28.6251 19.1137C29.091 19.3325 29.4532 19.6364 29.7117 20.0256C29.9703 20.4148 30.0995 20.8609 30.0995 21.3637C30.0995 21.6932 30.0342 22.0185 29.9035 22.3396C29.7757 22.6606 29.547 23.0171 29.2174 23.4092C28.8879 23.7984 28.4234 24.2657 27.824 24.8111L26.5498 26.0597V26.1194H30.2146V27.6279H23.993Z"
          fill="#FBFDFF"
          fillOpacity="0.46"
        />
      </g>
    </svg>
  )
}
